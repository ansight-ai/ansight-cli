using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ansight.Infrastructure;

namespace Ansight.Host.Runtime.Operations.Tools.SessionEvidence;

internal static class SessionArchiveExporter
{
    public static string ResolveDefaultArchivePath(IApplicationPaths applicationPaths, AppSessionSnapshot snapshot)
    {
        var directoryPath = Path.Combine(
            applicationPaths.ApplicationDataPath,
            SessionEvidenceDefaults.SessionExportsDirectoryName);
        var fileName = $"{FileNameUtil.Sanitize(snapshot.AppId)}-{FileNameUtil.Sanitize(snapshot.SessionId)}-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.zip";
        return Path.Combine(directoryPath, fileName);
    }

    public static void Export(
        IApplicationPaths applicationPaths,
        AppSessionSnapshot snapshot,
        string archiveFilePath,
        SessionBundleExportOptions options)
    {
        var directoryPath = Path.GetDirectoryName(archiveFilePath);
        if (string.IsNullOrWhiteSpace(directoryPath))
        {
            throw new InvalidDataException("Archive destination must include a directory.");
        }

        Directory.CreateDirectory(directoryPath);
        if (File.Exists(archiveFilePath))
        {
            File.Delete(archiveFilePath);
        }

        using var stream = PrivateStorageFile.Create(archiveFilePath);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
        WriteJsonEntry(archive, "manifest.json", BuildManifestPayload(snapshot));
        WriteJsonEntry(archive, "session/summary.json", PayloadJson.BuildSessionPayload(snapshot, isLive: !snapshot.IsHistorical));
        WriteJsonEntry(archive, "session/logs.json", PayloadJson.BuildSessionLogsResourcePayload(snapshot));
        WriteJsonEntry(archive, "session/annotations.json", PayloadJson.BuildSessionAnnotationsResourcePayload(snapshot));
        WriteJsonEntry(archive, "session/telemetry.json", PayloadJson.BuildSessionTelemetryResourcePayload(snapshot, "all"));
        WriteJsonEntry(archive, "session/visual-trees.json", PayloadJson.BuildSessionVisualTreesResourcePayload(snapshot));
        WriteJsonEntry(
            archive,
            "session/touches.json",
            new JsonObject
            {
                ["sessionId"] = snapshot.SessionId,
                ["appId"] = snapshot.AppId,
                ["touchCount"] = snapshot.Touches.Count,
                ["touches"] = PayloadJson.CreateJsonArray(snapshot.Touches.Select(touch => (JsonNode?)TouchReviewPayloads.BuildTouchPayload(touch)))
            });

        if (options.IncludeVisualTreePayloads)
        {
            foreach (var visualTree in snapshot.VisualTreeSnapshots)
            {
                WriteJsonEntry(
                    archive,
                    $"visual-trees/{SanitizeEntrySegment(visualTree.SnapshotId)}.json",
                    PayloadJson.BuildSessionVisualTreeSnapshotPayload(snapshot, visualTree));
            }
        }

        if (options.IncludeScreenshots)
        {
            AddScreenshotFiles(applicationPaths, snapshot, archive);
        }

        if (options.IncludeArtifactFiles)
        {
            AddArtifactFiles(applicationPaths, snapshot, archive);
        }
    }

    private static JsonObject BuildManifestPayload(AppSessionSnapshot snapshot)
    {
        return new JsonObject
        {
            ["schema"] = "ansight.session-evidence-bundle.v1",
            ["exportedAtUtc"] = DateTimeOffset.UtcNow,
            ["session"] = PayloadJson.BuildSessionPayload(snapshot, isLive: !snapshot.IsHistorical),
            ["counts"] = new JsonObject
            {
                ["logs"] = snapshot.Logs.Count,
                ["screenshots"] = snapshot.Images.Count,
                ["touches"] = snapshot.Touches.Count,
                ["annotations"] = snapshot.Annotations.Count,
                ["visualTrees"] = snapshot.VisualTreeSnapshots.Count,
                ["artifactSnapshots"] = snapshot.ArtifactSnapshots.Count,
                ["metricChannels"] = snapshot.MetricChannels.Count,
                ["metricSamples"] = snapshot.Metrics.Count
            }
        };
    }

    private static void AddScreenshotFiles(
        IApplicationPaths applicationPaths,
        AppSessionSnapshot snapshot,
        ZipArchive archive)
    {
        foreach (var frame in snapshot.Images.OrderBy(frame => frame.CapturedAtUtc))
        {
            var filePath = SessionFileLocator.ResolveScreenshotPath(applicationPaths, snapshot, frame);
            if (!File.Exists(filePath))
            {
                continue;
            }

            archive.CreateEntryFromFile(
                filePath,
                $"screenshots/{SessionImageArtifactPath.BuildFileName(frame)}",
                CompressionLevel.NoCompression);
        }
    }

    private static void AddArtifactFiles(
        IApplicationPaths applicationPaths,
        AppSessionSnapshot snapshot,
        ZipArchive archive)
    {
        var artifactsRootPath = SessionFileLocator.ResolveArtifactsRootPath(applicationPaths, snapshot);
        if (!Directory.Exists(artifactsRootPath))
        {
            return;
        }

        foreach (var filePath in Directory.EnumerateFiles(artifactsRootPath, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(artifactsRootPath, filePath)
                .Replace(Path.DirectorySeparatorChar, '/');
            archive.CreateEntryFromFile(
                filePath,
                $"artifacts/{relativePath}",
                CompressionLevel.NoCompression);
        }
    }

    private static void WriteJsonEntry(ZipArchive archive, string entryName, JsonObject payload)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open());
        writer.Write(JsonSerializer.Serialize(payload, JsonUtil.Pretty));
    }

    private static string SanitizeEntrySegment(string value)
        => FileNameUtil.Sanitize(string.IsNullOrWhiteSpace(value) ? "unknown" : value.Trim());
}
