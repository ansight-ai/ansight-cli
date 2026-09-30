namespace Ansight.Host.Runtime.SessionCaptureStorage;

using AppLifecycleState = global::Ansight.AppLifecycleState;
using Ansight.Host;
using Ansight.Pairing.Models;
using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Threading.Channels;

internal static partial class SessionSnapshotReader
{
    public static IReadOnlyList<LogEntry> LoadLogs(
        string filePath,
        string segmentDirectoryPath,
        IProgress<SessionLoadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ReportLoadProgress(progress, SessionLoadStage.Logs, 0, "Reading base session logs...");
        var appendFilePath = Path.Combine(
            Path.GetDirectoryName(filePath) ?? string.Empty,
            LogsAppendFileName);
        var segmentFilePaths = Directory.Exists(segmentDirectoryPath)
            ? Directory.EnumerateFiles(segmentDirectoryPath, "segment-*.json", SearchOption.AllDirectories)
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToArray()
            : Array.Empty<string>();
        var hasAppendFile = File.Exists(appendFilePath);
        var totalFileCount = 1 + segmentFilePaths.Length + (hasAppendFile ? 1 : 0);
        var processedFileCount = 0;
        var baseDocument = TryLoadDocument<SessionLogsBlobDocument>(filePath, cancellationToken);
        var baseLogs = baseDocument?.Logs ?? Array.Empty<LogEntry>();
        var baseSavedAtUtc = baseDocument?.SavedAtUtc ?? DateTimeOffset.MinValue;
        processedFileCount++;
        ReportLoadProgress(
            progress,
            SessionLoadStage.Logs,
            ResolveFileLoadProgress(processedFileCount, totalFileCount),
            $"Loaded base session logs ({baseLogs.Count:N0} entries).");

        if (segmentFilePaths.Length == 0 && !hasAppendFile)
        {
            ReportLoadProgress(progress, SessionLoadStage.Logs, 1, FormatLoadCount(baseLogs.Count, "log entry", "log entries"));
            return baseLogs;
        }

        var logs = new List<LogEntry>(baseLogs.Count);
        logs.AddRange(baseLogs);
        for (var index = 0; index < segmentFilePaths.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReportFileLoadProgress(
                progress,
                SessionLoadStage.Logs,
                processedFileCount,
                totalFileCount,
                $"Reading log segment {index + 1:N0} of {segmentFilePaths.Length:N0}...");
            var segmentDocument = TryLoadDocument<SessionLogsBlobDocument>(segmentFilePaths[index], cancellationToken);
            if (segmentDocument is null
                || segmentDocument.SavedAtUtc <= baseSavedAtUtc
                || segmentDocument.Logs.Count == 0)
            {
                processedFileCount++;
                ReportFileLoadProgress(
                    progress,
                    SessionLoadStage.Logs,
                    processedFileCount,
                    totalFileCount,
                    $"Loaded log segment {index + 1:N0} of {segmentFilePaths.Length:N0}.");
                continue;
            }

            logs.AddRange(segmentDocument.Logs);
            processedFileCount++;
            ReportFileLoadProgress(
                progress,
                SessionLoadStage.Logs,
                processedFileCount,
                totalFileCount,
                $"Loaded log segment {index + 1:N0} of {segmentFilePaths.Length:N0}.");
        }

        if (hasAppendFile)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReportFileLoadProgress(
                progress,
                SessionLoadStage.Logs,
                processedFileCount,
                totalFileCount,
                "Reading appended session logs...");
            try
            {
                foreach (var line in File.ReadLines(appendFilePath))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }

                    try
                    {
                        var appendDocument = JsonSerializer.Deserialize<SessionLogsBlobDocument>(line, JsonUtil.Compact);
                        if (appendDocument is not null
                            && appendDocument.SavedAtUtc > baseSavedAtUtc
                            && appendDocument.Logs.Count > 0)
                        {
                            logs.AddRange(appendDocument.Logs);
                        }
                    }
                    catch (JsonException)
                    {
                        // A process crash can leave one partial trailing line; prior complete batches remain valid.
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Preserve the canonical logs and any legacy segments that were already loaded.
            }

            processedFileCount++;
            ReportFileLoadProgress(
                progress,
                SessionLoadStage.Logs,
                processedFileCount,
                totalFileCount,
                "Loaded appended session logs.");
        }

        IReadOnlyList<LogEntry> loadedLogs = logs.Count == baseLogs.Count ? baseLogs : logs;
        if (!IsTimestampOrdered(loadedLogs))
        {
            loadedLogs = loadedLogs.OrderBy(entry => entry.TimestampUtc).ToArray();
        }

        ReportLoadProgress(progress, SessionLoadStage.Logs, 1, FormatLoadCount(loadedLogs.Count, "log entry", "log entries"));
        return loadedLogs;
    }

    public static bool IsTimestampOrdered(IReadOnlyList<LogEntry> entries)
    {
        for (var index = 1; index < entries.Count; index++)
        {
            if (entries[index - 1].TimestampUtc > entries[index].TimestampUtc)
            {
                return false;
            }
        }

        return true;
    }

    private static IReadOnlyList<SessionLogStream> LoadLogStreams(
        string filePath,
        IReadOnlyList<LogEntry> logs,
        CancellationToken cancellationToken = default)
    {
        var descriptors = TryLoadDocument<SessionLogStreamsBlobDocument>(filePath, cancellationToken)?.Streams;
        if (descriptors is not { Count: > 0 })
        {
            return SessionLogStreams.Normalize(Array.Empty<SessionLogStream>(), logs);
        }

        return descriptors
            .Select(stream =>
            {
                var entries = logs
                    .Where(log => string.Equals(log.StreamId, stream.StreamId, StringComparison.Ordinal))
                    .ToArray();
                return new SessionLogStream
                {
                    StreamId = stream.StreamId,
                    Kind = stream.Kind,
                    DisplayName = stream.DisplayName,
                    Status = stream.Status,
                    StartedUtc = stream.StartedUtc,
                    EndedUtc = stream.EndedUtc,
                    StatusMessage = stream.StatusMessage,
                    Metadata = stream.Metadata,
                    Entries = entries,
                    TotalEntryCount = Math.Max(stream.TotalEntryCount, entries.Length),
                    RetainedEntryStartIndex = 0
                };
            })
            .ToArray();
    }

    private static IReadOnlyList<SessionMetricChannel> LoadMetricChannels(string filePath, CancellationToken cancellationToken = default)
    {
        return TryLoadDocument<SessionMetricChannelsBlobDocument>(filePath, cancellationToken)?.Channels ?? Array.Empty<SessionMetricChannel>();
    }

    public static IReadOnlyList<SessionMetricSample> LoadTelemetry(
        string telemetryDirectoryPath,
        IProgress<SessionLoadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ReportLoadProgress(progress, SessionLoadStage.Telemetry, 0, "Reading telemetry samples...");
        if (!Directory.Exists(telemetryDirectoryPath))
        {
            ReportLoadProgress(progress, SessionLoadStage.Telemetry, 1, "No telemetry samples found.");
            return Array.Empty<SessionMetricSample>();
        }

        var channelFilePaths = Directory.EnumerateFiles(telemetryDirectoryPath, "channel-???.json", SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var segmentFilePaths = Directory.EnumerateFiles(telemetryDirectoryPath, "channel-???.segment-*.json", SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var appendFilePath = Path.Combine(telemetryDirectoryPath, TelemetryAppendFileName);
        var hasAppendFile = File.Exists(appendFilePath);
        var totalFileCount = channelFilePaths.Length + segmentFilePaths.Length + (hasAppendFile ? 1 : 0);
        var processedFileCount = 0;
        var metrics = new List<SessionMetricSample>();
        foreach (var filePath in channelFilePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReportFileLoadProgress(
                progress,
                SessionLoadStage.Telemetry,
                processedFileCount,
                totalFileCount,
                $"Reading telemetry channel {processedFileCount + 1:N0} of {totalFileCount:N0}...");
            if (!TryParseTelemetryChannelId(filePath, out var channelId))
            {
                processedFileCount++;
                continue;
            }

            var document = TryLoadDocument<SessionTelemetryBlobDocument>(filePath, cancellationToken);
            if (document?.Metrics is null
                || document.Metrics.Count == 0
                || document.ChannelId != channelId)
            {
                processedFileCount++;
                ReportFileLoadProgress(
                    progress,
                    SessionLoadStage.Telemetry,
                    processedFileCount,
                    totalFileCount,
                    $"Loaded telemetry channel {processedFileCount:N0} of {totalFileCount:N0}.");
                continue;
            }

            metrics.AddRange(document.Metrics);
            processedFileCount++;
            ReportFileLoadProgress(
                progress,
                SessionLoadStage.Telemetry,
                processedFileCount,
                totalFileCount,
                $"Loaded telemetry channel {processedFileCount:N0} of {totalFileCount:N0}.");
        }

        foreach (var filePath in segmentFilePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReportFileLoadProgress(
                progress,
                SessionLoadStage.Telemetry,
                processedFileCount,
                totalFileCount,
                $"Reading telemetry segment {processedFileCount + 1:N0} of {totalFileCount:N0}...");
            if (!TryParseTelemetrySegmentChannelId(filePath, out var channelId))
            {
                processedFileCount++;
                continue;
            }

            var document = TryLoadDocument<SessionTelemetryBlobDocument>(filePath, cancellationToken);
            if (document?.Metrics is null
                || document.Metrics.Count == 0
                || document.ChannelId != channelId)
            {
                processedFileCount++;
                ReportFileLoadProgress(
                    progress,
                    SessionLoadStage.Telemetry,
                    processedFileCount,
                    totalFileCount,
                    $"Loaded telemetry segment {processedFileCount:N0} of {totalFileCount:N0}.");
                continue;
            }

            metrics.AddRange(document.Metrics);
            processedFileCount++;
            ReportFileLoadProgress(
                progress,
                SessionLoadStage.Telemetry,
                processedFileCount,
                totalFileCount,
                $"Loaded telemetry segment {processedFileCount:N0} of {totalFileCount:N0}.");
        }

        if (hasAppendFile)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReportFileLoadProgress(
                progress,
                SessionLoadStage.Telemetry,
                processedFileCount,
                totalFileCount,
                "Reading appended telemetry batches...");
            try
            {
                foreach (var line in File.ReadLines(appendFilePath))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }

                    try
                    {
                        var document = JsonSerializer.Deserialize<SessionTelemetryAppendDocument>(line, JsonUtil.Compact);
                        if (document is not null)
                        {
                            if (document.ReplaceAll)
                            {
                                metrics.Clear();
                            }

                            if (document.Metrics.Count > 0)
                            {
                                metrics.AddRange(document.Metrics);
                            }
                        }
                    }
                    catch (JsonException)
                    {
                        // A process crash can leave one partial trailing line; prior complete batches remain valid.
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Preserve any canonical and legacy telemetry that was already loaded.
            }

            processedFileCount++;
            ReportFileLoadProgress(
                progress,
                SessionLoadStage.Telemetry,
                processedFileCount,
                totalFileCount,
                "Loaded appended telemetry batches.");
        }

        var loadedMetrics = metrics
            .DistinctBy(metric => new MetricDeduplicationKey(
                metric.ChannelId,
                metric.Value,
                metric.CapturedAtUtc))
            .OrderBy(metric => metric.CapturedAtUtc)
            .ThenBy(metric => metric.ChannelId)
            .ToArray();
        ReportLoadProgress(progress, SessionLoadStage.Telemetry, 1, FormatLoadCount(loadedMetrics.Length, "telemetry sample", "telemetry samples"));
        return loadedMetrics;
    }

    private static void ReportLoadProgress(
        IProgress<SessionLoadProgress>? progress,
        SessionLoadStage stage,
        double stageProgress,
        string statusText)
    {
        if (progress is null)
        {
            return;
        }

        var stageIndex = Array.IndexOf(FullSnapshotLoadStages, stage);
        var totalStageCount = FullSnapshotLoadStages.Length;
        var normalizedStageProgress = Math.Clamp(stageProgress, 0, 1);
        var overallProgress = stage == SessionLoadStage.Complete || stageIndex < 0
            ? 1
            : (stageIndex + normalizedStageProgress) / totalStageCount;
        progress.Report(new SessionLoadProgress(
            stage,
            ResolveLoadStageName(stage),
            string.IsNullOrWhiteSpace(statusText) ? ResolveLoadStageName(stage) : statusText.Trim(),
            normalizedStageProgress,
            Math.Clamp(overallProgress, 0, 1),
            stageIndex < 0 ? totalStageCount : stageIndex + 1,
            totalStageCount));
    }

    private static void ReportFileLoadProgress(
        IProgress<SessionLoadProgress>? progress,
        SessionLoadStage stage,
        int processedFileCount,
        int totalFileCount,
        string statusText)
    {
        if (!ShouldReportFileLoadProgress(processedFileCount, totalFileCount))
        {
            return;
        }

        ReportLoadProgress(
            progress,
            stage,
            ResolveFileLoadProgress(processedFileCount, totalFileCount),
            statusText);
    }

    private static bool ShouldReportFileLoadProgress(int processedFileCount, int totalFileCount)
    {
        if (totalFileCount <= DetailedLoadProgressFileLimit)
        {
            return true;
        }

        return processedFileCount <= 0
               || processedFileCount >= totalFileCount
               || processedFileCount % LoadProgressFileReportInterval == 0;
    }

    private static string ResolveLoadStageName(SessionLoadStage stage)
    {
        return stage switch
        {
            SessionLoadStage.Header => "Header",
            SessionLoadStage.DeviceProfile => "Device profile",
            SessionLoadStage.Analyses => "Analyses",
            SessionLoadStage.Annotations => "Annotations",
            SessionLoadStage.Images => "Screenshots",
            SessionLoadStage.Touches => "Touches",
            SessionLoadStage.NetworkRequests => "Network requests",
            SessionLoadStage.VisualTreeSnapshots => "Visual trees",
            SessionLoadStage.ArtifactSnapshots => "Artifacts",
            SessionLoadStage.Logs => "Logs",
            SessionLoadStage.MetricChannels => "Telemetry channels",
            SessionLoadStage.Telemetry => "Telemetry",
            SessionLoadStage.Complete => "Complete",
            _ => "Session content"
        };
    }

    private static double ResolveFileLoadProgress(int processedFileCount, int totalFileCount)
    {
        return totalFileCount <= 0
            ? 1
            : Math.Clamp(processedFileCount / (double)totalFileCount, 0, 1);
    }

    private static string BuildDeviceProfileLoadStatus(DeviceProfileLoadResult deviceProfile)
    {
        return deviceProfile.Profile is not null || !string.IsNullOrWhiteSpace(deviceProfile.ProfileJson)
            ? "Device profile loaded."
            : "No device profile found.";
    }

    private static string FormatLoadCount(int count, string singular, string plural)
    {
        var label = count == 1 ? singular : plural;
        return $"Loaded {count:N0} {label}.";
    }

    private static bool TryParseTelemetryChannelId(string filePath, out byte channelId)
    {
        channelId = 0;

        var fileName = Path.GetFileName(filePath);
        if (!fileName.StartsWith("channel-", StringComparison.OrdinalIgnoreCase)
            || !fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var channelText = fileName["channel-".Length..^".json".Length];
        return byte.TryParse(channelText, out channelId);
    }

    private static bool TryParseTelemetrySegmentChannelId(string filePath, out byte channelId)
    {
        channelId = 0;

        var fileName = Path.GetFileName(filePath);
        const string marker = ".segment-";
        if (!fileName.StartsWith("channel-", StringComparison.OrdinalIgnoreCase)
            || !fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var markerIndex = fileName.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex <= "channel-".Length)
        {
            return false;
        }

        var channelText = fileName["channel-".Length..markerIndex];
        return byte.TryParse(channelText, out channelId);
    }

    public static T? TryLoadDocument<T>(string filePath)
        where T : class
    {
        return TryLoadDocument<T>(filePath, CancellationToken.None);
    }

    private static T? TryLoadDocument<T>(string filePath, CancellationToken cancellationToken)
        where T : class
    {
        if (!File.Exists(filePath))
        {
            return null;
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var stream = File.OpenRead(filePath);
            var document = JsonSerializer.Deserialize<T>(stream, JsonUtil.Compact);
            cancellationToken.ThrowIfCancellationRequested();
            return document;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    public static AppSessionSnapshot CloneAsHistorical(AppSessionSnapshot snapshot)
    {
        return new AppSessionSnapshot
        {
            SessionId = snapshot.SessionId,
            AppId = snapshot.AppId,
            ClientName = snapshot.ClientName,
            RemoteAddress = snapshot.RemoteAddress,
            Name = snapshot.Name,
            CreatedUtc = snapshot.CreatedUtc,
            ConfigId = snapshot.ConfigId,
            ProcessSessionId = snapshot.ProcessSessionId,
            Status = snapshot.Status,
            LastUpdatedUtc = snapshot.LastUpdatedUtc,
            IsHistorical = true,
            CacheSizeBytes = snapshot.CacheSizeBytes,
            IsPinned = snapshot.IsPinned,
            Author = snapshot.Author,
            ReplaySource = snapshot.ReplaySource,
            CaptureSource = snapshot.CaptureSource,
            SdkVersion = SessionSdkVersion.Resolve(snapshot.SdkVersion, snapshot.DeviceProfile, snapshot.DeviceProfileJson),
            Tags = snapshot.Tags.ToArray(),
            Notes = snapshot.Notes,
            CustomProperties = SessionSnapshotCloner.CloneCustomProperties(snapshot.CustomProperties),
            AppState = snapshot.AppState,
            AppStateChangedUtc = snapshot.AppStateChangedUtc,
            DeviceProfile = snapshot.DeviceProfile,
            DeviceProfileJson = snapshot.DeviceProfileJson,
            AppIcon = snapshot.AppIcon,
            AppToolCatalog = SessionSnapshotCloner.CloneAppToolCatalog(snapshot.AppToolCatalog),
            Analyses = snapshot.Analyses,
            Annotations = snapshot.Annotations,
            AgentTaskLinks = snapshot.AgentTaskLinks,
            Images = snapshot.Images,
            Touches = snapshot.Touches,
            VisualTreeSnapshots = snapshot.VisualTreeSnapshots,
            ArtifactSnapshots = snapshot.ArtifactSnapshots,
            ApplicationEvents = snapshot.ApplicationEvents,
            NetworkRequests = snapshot.NetworkRequests,
            LogStreams = snapshot.LogStreams,
            Logs = snapshot.Logs,
            TotalLogCount = snapshot.TotalLogCount,
            RetainedLogStartIndex = snapshot.RetainedLogStartIndex,
            MetricChannels = snapshot.MetricChannels,
            Metrics = snapshot.Metrics,
            TotalApplicationEventCount = snapshot.TotalApplicationEventCount,
            TotalNetworkRequestCount = snapshot.TotalNetworkRequestCount
        };
    }

    public static SessionPathLayout GetSessionLayoutFromSummaryPath(string summaryFilePath)
    {
        var sessionDirectoryPath = Path.GetDirectoryName(summaryFilePath)
                                   ?? throw new InvalidOperationException("Session summary path is invalid.");
        var appDirectoryPath = Path.GetDirectoryName(sessionDirectoryPath)
                               ?? throw new InvalidOperationException("Session summary path is invalid.");
        return new SessionPathLayout(
            appDirectoryPath,
            sessionDirectoryPath,
            summaryFilePath,
            Path.Combine(sessionDirectoryPath, LogsBlobFileName),
            Path.Combine(sessionDirectoryPath, LogStreamsBlobFileName),
            Path.Combine(sessionDirectoryPath, LogSegmentsDirectoryName),
            Path.Combine(sessionDirectoryPath, DeviceProfileBlobFileName),
            Path.Combine(sessionDirectoryPath, AppToolCatalogBlobFileName),
            Path.Combine(sessionDirectoryPath, AnalysesBlobFileName),
            Path.Combine(sessionDirectoryPath, AnnotationsBlobFileName),
            Path.Combine(sessionDirectoryPath, AgentTaskLinksBlobFileName),
            Path.Combine(sessionDirectoryPath, ImagesBlobFileName),
            Path.Combine(sessionDirectoryPath, TouchesBlobFileName),
            Path.Combine(sessionDirectoryPath, ApplicationEventsBlobFileName),
            Path.Combine(sessionDirectoryPath, NetworkDirectoryName, NetworkRequestsDirectoryName),
            Path.Combine(sessionDirectoryPath, VisualTreesDirectoryName),
            Path.Combine(sessionDirectoryPath, ArtifactsDirectoryName),
            Path.Combine(sessionDirectoryPath, MetricChannelsBlobFileName),
            Path.Combine(sessionDirectoryPath, TelemetryDirectoryName),
            Path.Combine(sessionDirectoryPath, ImagesDirectoryName));
    }

    private static string GetTouchesAppendFilePath(SessionPathLayout layout)
        => Path.Combine(layout.SessionDirectoryPath, TouchesAppendFileName);

    private static long GetDirectorySize(string directoryPath)
    {
        if (string.IsNullOrWhiteSpace(directoryPath) || !Directory.Exists(directoryPath))
        {
            return 0;
        }

        long size = 0;
        try
        {
            foreach (var filePath in Directory.EnumerateFiles(directoryPath, "*", SearchOption.AllDirectories))
            {
                try
                {
                    size += new FileInfo(filePath).Length;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return size;
        }

        return size;
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
