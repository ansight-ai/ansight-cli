namespace Ansight.Host.Runtime.SessionCaptureStorage;

using System.Globalization;
using System.IO.Compression;
using Ansight.Pairing.Models;

internal static class SessionCaptureArchiveCodec
{
    private const string SessionSummaryFileName = "session.json";
    private const string SessionCacheArchiveSuffix = ".session-cache.zip";
    private const string SessionCacheCompactionResetFileName = ".session-cache-compaction-reset";
    private const string DeviceProfileBlobFileName = "device-profile.json";
    private const string AnalysesBlobFileName = "analyses.json";
    private const string AnnotationsBlobFileName = "annotations.json";
    private const string AgentTaskLinksBlobFileName = "agent-tasks.json";
    private const string ImagesBlobFileName = "images.json";

    internal static SessionCaptureSummary? TryLoadSessionArchiveSummary(string archiveFilePath)
    {
        try
        {
            using var archive = ZipFile.OpenRead(archiveFilePath);
            return TryLoadArchiveDocument<SessionCaptureSummaryDocument>(archive, SessionSummaryFileName)?.Session;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    internal static AppSessionSnapshot CreateArchivedSummarySnapshot(
        string archiveFilePath,
        SessionCaptureSummary summary,
        long archiveSizeBytes)
    {
        SessionDeviceProfileBlobDocument? deviceProfileDocument = null;
        SessionAnalysesBlobDocument? analysesDocument = null;
        SessionAnnotationsBlobDocument? annotationsDocument = null;
        SessionAgentTaskLinksBlobDocument? taskLinksDocument = null;
        SessionImagesBlobDocument? imagesDocument = null;
        try
        {
            using var archive = ZipFile.OpenRead(archiveFilePath);
            deviceProfileDocument = TryLoadArchiveDocument<SessionDeviceProfileBlobDocument>(archive, DeviceProfileBlobFileName);
            analysesDocument = TryLoadArchiveDocument<SessionAnalysesBlobDocument>(archive, AnalysesBlobFileName);
            annotationsDocument = TryLoadArchiveDocument<SessionAnnotationsBlobDocument>(archive, AnnotationsBlobFileName);
            taskLinksDocument = TryLoadArchiveDocument<SessionAgentTaskLinksBlobDocument>(archive, AgentTaskLinksBlobFileName);
            imagesDocument = TryLoadArchiveDocument<SessionImagesBlobDocument>(archive, ImagesBlobFileName);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
        }

        var profileJson = NormalizeJson(deviceProfileDocument?.ProfileJson);
        DeviceAppProfile? deviceProfile = null;
        if (!string.IsNullOrWhiteSpace(profileJson))
        {
            try
            {
                deviceProfile = JsonSerializer.Deserialize<DeviceAppProfile>(profileJson, JsonUtil.Compact);
            }
            catch
            {
            }
        }

        var analyses = analysesDocument?.Analyses ?? Array.Empty<SessionAnalysisRecord>();
        var annotations = annotationsDocument?.Annotations ?? Array.Empty<SessionAnnotation>();
        var images = imagesDocument?.Images ?? Array.Empty<SessionImageFrame>();
        return new AppSessionSnapshot
        {
            SessionId = summary.SessionId,
            AppId = summary.AppId,
            ClientName = summary.ClientName,
            RemoteAddress = summary.RemoteAddress,
            Name = summary.Name,
            CreatedUtc = summary.CreatedUtc,
            ConfigId = summary.ConfigId,
            ProcessSessionId = summary.ProcessSessionId,
            Status = summary.Status,
            LastUpdatedUtc = summary.LastUpdatedUtc,
            IsHistorical = true,
            CacheSizeBytes = archiveSizeBytes,
            IsPinned = summary.IsPinned,
            Author = summary.Author,
            ReplaySource = summary.ReplaySource,
            CaptureSource = summary.CaptureSource,
            SdkVersion = SessionSdkVersion.Resolve(summary.SdkVersion, deviceProfile, profileJson),
            Tags = summary.Tags?.ToArray() ?? [],
            Notes = summary.Notes,
            CustomProperties = SessionSnapshotCloner.CloneCustomProperties(summary.CustomProperties),
            AppState = summary.AppState,
            AppStateChangedUtc = summary.AppStateChangedUtc,
            DeviceProfile = deviceProfile,
            DeviceProfileJson = profileJson,
            AppIcon = summary.AppIcon,
            Analyses = analyses,
            Annotations = annotations,
            AgentTaskLinks = taskLinksDocument?.Tasks ?? Array.Empty<SessionAgentTaskLink>(),
            Images = images,
            TotalLogCount = summary.LogCount,
            RetainedLogStartIndex = Math.Max(0, summary.LogCount),
            MetricChannels = Array.Empty<SessionMetricChannel>(),
            Metrics = Array.Empty<SessionMetricSample>(),
            TotalAnnotationCount = Math.Max(summary.AnnotationCount, annotations.Count),
            TotalImageCount = Math.Max(summary.ImageCount, images.Count),
            TotalMetricChannelCount = summary.MetricChannelCount,
            TotalMetricSampleCount = summary.MetricSampleCount,
            TotalApplicationEventCount = summary.ApplicationEventCount,
            TotalNetworkRequestCount = summary.NetworkRequestCount
        };
    }

    private static T? TryLoadArchiveDocument<T>(ZipArchive archive, string entryName)
        where T : class
    {
        var entry = archive.GetEntry(entryName);
        if (entry is null)
        {
            return null;
        }

        try
        {
            using var stream = entry.Open();
            return JsonSerializer.Deserialize<T>(stream, JsonUtil.Compact);
        }
        catch
        {
            return null;
        }
    }

    internal static string GetSessionArchivePath(string sessionDirectoryPath)
        => $"{sessionDirectoryPath}{SessionCacheArchiveSuffix}";

    internal static bool IsSessionPastCompactionCutoff(
        SessionCaptureSummary summary,
        string summaryFilePath,
        DateTimeOffset cutoffUtc)
    {
        var referenceUtc = summary.LastUpdatedUtc;
        var sessionDirectoryPath = Path.GetDirectoryName(summaryFilePath);
        if (!string.IsNullOrWhiteSpace(sessionDirectoryPath))
        {
            var resetFilePath = Path.Combine(sessionDirectoryPath, SessionCacheCompactionResetFileName);
            try
            {
                var resetText = File.ReadAllText(resetFilePath).Trim();
                if (DateTimeOffset.TryParse(
                        resetText,
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind,
                        out var resetUtc)
                    && resetUtc > referenceUtc)
                {
                    referenceUtc = resetUtc;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        return referenceUtc < cutoffUtc;
    }

    internal static void ResetSessionCompactionAge(
        string sessionDirectoryPath,
        DateTimeOffset resetUtc)
    {
        var resetFilePath = Path.Combine(sessionDirectoryPath, SessionCacheCompactionResetFileName);
        try
        {
            File.WriteAllText(
                resetFilePath,
                resetUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    internal static string GetSessionDirectoryPathFromArchivePath(string archiveFilePath)
    {
        if (!archiveFilePath.EndsWith(SessionCacheArchiveSuffix, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The session cache archive path has an unsupported suffix.");
        }

        return archiveFilePath[..^SessionCacheArchiveSuffix.Length];
    }

    private static string? NormalizeJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            return JsonSerializer.Serialize(document.RootElement, JsonUtil.Compact);
        }
        catch
        {
            return json.Trim();
        }
    }
}
