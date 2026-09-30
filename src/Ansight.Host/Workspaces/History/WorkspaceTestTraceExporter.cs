using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ansight.Host.Workspaces.History;

public sealed record WorkspaceTestTraceExportResult(
    bool IsSuccess,
    string Message,
    string RunId,
    string OutputPath,
    int TraceCount,
    int SessionCount);

public sealed class WorkspaceTestTraceExporter
{
    private readonly RuntimeCoordinator runtime;
    private readonly Func<string, string?, WorkspaceTestHistoryInspection?> historyInspector;

    internal WorkspaceTestTraceExporter(
        RuntimeCoordinator runtime,
        Func<string, string?, WorkspaceTestHistoryInspection?> historyInspector)
    {
        this.runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        this.historyInspector = historyInspector ?? throw new ArgumentNullException(nameof(historyInspector));
    }

    public async Task<WorkspaceTestTraceExportResult> ExportAsync(
        string runId,
        string outputPath,
        string? appId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        var normalizedRunId = runId.Trim();
        var normalizedOutputPath = Path.GetFullPath(outputPath.Trim());
        var inspection = historyInspector(normalizedRunId, appId);
        if (inspection is null)
        {
            return new WorkspaceTestTraceExportResult(
                false,
                $"Test or batch run '{normalizedRunId}' was not found in local history.",
                normalizedRunId,
                normalizedOutputPath,
                0,
                0);
        }

        var untracedRunIds = inspection.Runs
            .Where(static entry => entry.Audit.TraceEnabled == false)
            .Select(static entry => entry.Audit.RunId)
            .ToArray();
        if (untracedRunIds.Length > 0)
        {
            return new WorkspaceTestTraceExportResult(
                false,
                $"Run(s) {string.Join(", ", untracedRunIds)} did not capture a trace. Rerun with --trace before exporting.",
                normalizedRunId,
                normalizedOutputPath,
                0,
                0);
        }

        var outputDirectoryPath = Path.GetDirectoryName(normalizedOutputPath);
        if (string.IsNullOrWhiteSpace(outputDirectoryPath))
        {
            return new WorkspaceTestTraceExportResult(
                false,
                "The trace export destination must include a directory.",
                normalizedRunId,
                normalizedOutputPath,
                0,
                0);
        }

        var temporaryDirectoryPath = Path.Combine(
            Path.GetTempPath(),
            $"ansight-trace-export-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectoryPath);
        try
        {
            var sessionExports = new JsonArray();
            var sessionArchivePaths = new List<WorkspaceTestTraceSessionArchive>();
            var sessionsById = new Dictionary<string, WorkspaceTestTraceSessionExport>(StringComparer.Ordinal);
            foreach (var entry in inspection.Runs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var audit = entry.Audit;
                if (!sessionsById.TryGetValue(audit.SessionId, out var sessionExport))
                {
                    var sessionFileName = $"{FileNameUtil.Sanitize(audit.SessionId)}.ansight-session.zip";
                    var sessionArchivePath = Path.Combine(temporaryDirectoryPath, sessionFileName);
                    var exportResult = await runtime.SessionArchives.ExportSessionArchiveAsync(
                            audit.SessionId,
                            sessionArchivePath,
                            cancellationToken)
                        .ConfigureAwait(false);
                    sessionExport = new WorkspaceTestTraceSessionExport(
                        exportResult.IsSuccess,
                        exportResult.Message,
                        sessionArchivePath,
                        $"sessions/{sessionFileName}");
                    sessionsById.Add(audit.SessionId, sessionExport);
                    if (sessionExport.IsSuccess)
                    {
                        sessionArchivePaths.Add(new WorkspaceTestTraceSessionArchive(
                            sessionExport.FilePath,
                            sessionExport.EntryName));
                    }
                }

                sessionExports.Add(new JsonObject
                {
                    ["runId"] = audit.RunId,
                    ["sessionId"] = audit.SessionId,
                    ["included"] = sessionExport.IsSuccess,
                    ["message"] = sessionExport.Message,
                    ["archive"] = sessionExport.IsSuccess
                        ? sessionExport.EntryName
                        : null
                });
            }

            Directory.CreateDirectory(outputDirectoryPath);
            if (File.Exists(normalizedOutputPath))
            {
                File.Delete(normalizedOutputPath);
            }

            using (var stream = File.Create(normalizedOutputPath))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                WriteTraceJsonEntry(
                    archive,
                    "manifest.json",
                    new JsonObject
                    {
                        ["schema"] = "ansight.agent-evaluation-bundle/v1",
                        ["exportedAtUtc"] = DateTimeOffset.UtcNow,
                        ["requestedRunId"] = inspection.RequestedRunId,
                        ["traceCount"] = inspection.Runs.Count,
                        ["sessionCount"] = sessionArchivePaths.Count,
                        ["batchIncluded"] = inspection.Batch is not null,
                        ["sessions"] = sessionExports
                    });

                if (inspection.Batch is not null)
                {
                    WriteTraceJsonEntry(archive, "trace/batch.json", inspection.Batch.Audit);
                }

                foreach (var entry in inspection.Runs)
                {
                    WriteTraceJsonEntry(
                        archive,
                        $"trace/{FileNameUtil.Sanitize(entry.Audit.RunId)}.json",
                        entry.Audit);
                    AddOcrTraceEvidenceFiles(archive, entry);
                }

                foreach (var sessionArchive in sessionArchivePaths)
                {
                    archive.CreateEntryFromFile(
                        sessionArchive.FilePath,
                        sessionArchive.EntryName,
                        CompressionLevel.NoCompression);
                }
            }

            return new WorkspaceTestTraceExportResult(
                true,
                $"Exported {inspection.Runs.Count:N0} trace(s) and {sessionArchivePaths.Count:N0} session archive(s).",
                normalizedRunId,
                normalizedOutputPath,
                inspection.Runs.Count,
                sessionArchivePaths.Count);
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or InvalidDataException
                                           or JsonException)
        {
            return new WorkspaceTestTraceExportResult(
                false,
                $"Unable to export the evaluation bundle: {exception.Message}",
                normalizedRunId,
                normalizedOutputPath,
                inspection.Runs.Count,
                0);
        }
        finally
        {
            try
            {
                Directory.Delete(temporaryDirectoryPath, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A stale temporary export can be removed by the operating system later.
            }
        }
    }

    private static void WriteTraceJsonEntry<T>(
        ZipArchive archive,
        string entryName,
        T value)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open());
        writer.Write(JsonSerializer.Serialize(value, JsonUtil.Pretty));
    }

    private static void AddOcrTraceEvidenceFiles(
        ZipArchive archive,
        SimulatorAgentRunHistoryEntry historyEntry)
    {
        var auditDirectoryPath = Path.GetDirectoryName(historyEntry.FilePath);
        if (string.IsNullOrWhiteSpace(auditDirectoryPath))
        {
            return;
        }

        var relativePaths = historyEntry.Audit.ToolCalls
            .SelectMany(static call => new[]
            {
                call.OcrEvidence?.ScreenshotPath,
                call.OcrEvidence?.ResultsPath
            })
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Select(static path => path!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        foreach (var relativePath in relativePaths)
        {
            var normalizedRelativePath = relativePath.Replace('\\', '/').TrimStart('/');
            if (Path.IsPathRooted(relativePath)
                || normalizedRelativePath.Split('/').Any(static segment => segment is "" or "." or ".."))
            {
                continue;
            }

            var sourcePath = Path.GetFullPath(Path.Combine(
                auditDirectoryPath,
                normalizedRelativePath.Replace('/', Path.DirectorySeparatorChar)));
            var sourceRelativePath = Path.GetRelativePath(auditDirectoryPath, sourcePath);
            if (sourceRelativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || string.Equals(sourceRelativePath, "..", StringComparison.Ordinal)
                || !File.Exists(sourcePath))
            {
                continue;
            }

            var archiveEntryName = $"trace/{normalizedRelativePath}";
            archive.CreateEntryFromFile(
                sourcePath,
                archiveEntryName,
                normalizedRelativePath.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                    ? CompressionLevel.Optimal
                    : CompressionLevel.NoCompression);
        }
    }

    private sealed record WorkspaceTestTraceSessionArchive(
        string FilePath,
        string EntryName);

    private sealed record WorkspaceTestTraceSessionExport(
        bool IsSuccess,
        string Message,
        string FilePath,
        string EntryName);
}
