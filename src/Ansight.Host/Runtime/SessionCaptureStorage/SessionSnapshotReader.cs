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
    private const string LogsBlobFileName = "logs.json";
    private const string LogsAppendFileName = "logs.jsonl";
    private const string LogStreamsBlobFileName = "log-streams.json";
    private const string LogSegmentsDirectoryName = "log-segments";
    private const string DeviceProfileBlobFileName = "device-profile.json";
    private const string AppToolCatalogBlobFileName = "app-tool-catalog.json";
    private const string AnalysesBlobFileName = "analyses.json";
    private const string AnnotationsBlobFileName = "annotations.json";
    private const string AgentTaskLinksBlobFileName = "agent-tasks.json";
    private const string ImagesBlobFileName = "images.json";
    private const string ImagesAppendFileName = "images.jsonl";
    private const string TouchesBlobFileName = "touches.json";
    private const string ApplicationEventsBlobFileName = "application-events.json";
    private const string NetworkDirectoryName = "network";
    private const string NetworkRequestsDirectoryName = "requests";
    private const string TouchesAppendFileName = "touches.jsonl";
    private const int DetailedLoadProgressFileLimit = 64;
    private const int LoadProgressFileReportInterval = 128;
    private const string MetricChannelsBlobFileName = "metric-channels.json";
    private const string TelemetryDirectoryName = "telemetry";
    private const string TelemetryAppendFileName = "segments.jsonl";
    private const string ImagesDirectoryName = "images";
    private const string VisualTreesDirectoryName = "visual-trees";
    private const string ArtifactsDirectoryName = "artifacts";
    private static readonly SessionLoadStage[] FullSnapshotLoadStages =
    [
        SessionLoadStage.Header,
        SessionLoadStage.DeviceProfile,
        SessionLoadStage.Analyses,
        SessionLoadStage.Annotations,
        SessionLoadStage.Images,
        SessionLoadStage.Touches,
        SessionLoadStage.NetworkRequests,
        SessionLoadStage.VisualTreeSnapshots,
        SessionLoadStage.ArtifactSnapshots,
        SessionLoadStage.Logs,
        SessionLoadStage.MetricChannels,
        SessionLoadStage.Telemetry
    ];

    public static AppSessionSnapshot? LoadSummary(string filePath, SessionCaptureSummary summary)
    {
        return LoadSnapshot(filePath, includeHeavyBlobs: false, knownSummary: summary);
    }

    public static AppSessionSnapshot? LoadFullSnapshot(
        string filePath,
        IProgress<SessionLoadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return LoadSnapshot(filePath, includeHeavyBlobs: true, progress, cancellationToken);
    }

    private static AppSessionSnapshot? LoadSnapshot(
        string summaryFilePath,
        bool includeHeavyBlobs,
        IProgress<SessionLoadProgress>? progress = null,
        CancellationToken cancellationToken = default,
        SessionCaptureSummary? knownSummary = null)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReportLoadProgress(progress, SessionLoadStage.Header, 0, "Reading session header...");
            var summary = knownSummary;
            if (summary is null)
            {
                var summaryDocument = TryLoadDocument<SessionCaptureSummaryDocument>(summaryFilePath, cancellationToken);
                if (summaryDocument?.Session is null)
                {
                    return null;
                }

                summary = summaryDocument.Session;
            }

            var layout = GetSessionLayoutFromSummaryPath(summaryFilePath);
            ReportLoadProgress(progress, SessionLoadStage.Header, 1, "Session header loaded.");

            ReportLoadProgress(progress, SessionLoadStage.DeviceProfile, 0, "Reading device profile...");
            var deviceProfileResult = LoadDeviceProfile(layout.DeviceProfileFilePath, cancellationToken);
            ReportLoadProgress(progress, SessionLoadStage.DeviceProfile, 1, BuildDeviceProfileLoadStatus(deviceProfileResult));
            var appToolCatalog = LoadAppToolCatalog(layout.AppToolCatalogFilePath, cancellationToken);

            ReportLoadProgress(progress, SessionLoadStage.Analyses, 0, "Reading saved analyses...");
            var analyses = LoadAnalyses(layout.AnalysesFilePath, cancellationToken);
            ReportLoadProgress(progress, SessionLoadStage.Analyses, 1, FormatLoadCount(analyses.Count, "saved analysis", "saved analyses"));

            ReportLoadProgress(progress, SessionLoadStage.Annotations, 0, "Reading annotations...");
            var annotations = LoadAnnotations(layout.AnnotationsFilePath, cancellationToken);
            ReportLoadProgress(progress, SessionLoadStage.Annotations, 1, FormatLoadCount(annotations.Count, "annotation", "annotations"));
            var agentTaskLinks = LoadAgentTaskLinks(layout.AgentTaskLinksFilePath, cancellationToken);

            ReportLoadProgress(progress, SessionLoadStage.Images, 0, "Resolving screenshot frames...");
            var images = LoadImages(layout.ImagesFilePath, layout.CapturedImagesDirectoryPath, progress, cancellationToken);
            ReportLoadProgress(progress, SessionLoadStage.Images, 1, FormatLoadCount(images.Count, "screenshot frame", "screenshot frames"));

            var touches = includeHeavyBlobs
                ? LoadTouches(layout, progress, cancellationToken)
                : Array.Empty<SessionTouchInputRecord>();
            var applicationEvents = includeHeavyBlobs
                ? LoadApplicationEvents(layout.ApplicationEventsFilePath, cancellationToken)
                : Array.Empty<SessionApplicationEvent>();
            var networkRequests = includeHeavyBlobs
                ? LoadNetworkRequests(layout.NetworkRequestsDirectoryPath, progress, cancellationToken)
                : Array.Empty<SessionNetworkRequest>();
            var visualTreeSnapshots = includeHeavyBlobs
                ? LoadVisualTreeSnapshots(layout.VisualTreesDirectoryPath, progress, cancellationToken)
                : Array.Empty<SessionVisualTreeSnapshot>();
            var artifactSnapshots = includeHeavyBlobs
                ? LoadArtifactSnapshots(layout.ArtifactsDirectoryPath, progress, cancellationToken)
                : Array.Empty<SessionArtifactSnapshot>();
            var appIcon = LoadAppIcon(layout.SessionDirectoryPath, summary.AppIcon);
            var cacheSizeBytes = summary.CacheSizeBytes > 0
                ? summary.CacheSizeBytes
                : GetDirectorySize(layout.SessionDirectoryPath);
            var logs = includeHeavyBlobs ? LoadLogs(layout.LogsFilePath, layout.LogSegmentsDirectoryPath, progress, cancellationToken) : Array.Empty<LogEntry>();
            var logStreams = includeHeavyBlobs
                ? LoadLogStreams(layout.LogStreamsFilePath, logs, cancellationToken)
                : Array.Empty<SessionLogStream>();

            IReadOnlyList<SessionMetricChannel> channels = Array.Empty<SessionMetricChannel>();
            IReadOnlyList<SessionMetricSample> metrics = Array.Empty<SessionMetricSample>();
            if (includeHeavyBlobs)
            {
                ReportLoadProgress(progress, SessionLoadStage.MetricChannels, 0, "Reading telemetry channels...");
                channels = LoadMetricChannels(layout.MetricChannelsFilePath, cancellationToken);
                ReportLoadProgress(progress, SessionLoadStage.MetricChannels, 1, FormatLoadCount(channels.Count, "telemetry channel", "telemetry channels"));
                metrics = LoadTelemetry(layout.TelemetryDirectoryPath, progress, cancellationToken);
            }

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
                LastUpdatedUtc = SessionCaptureEndResolver.Resolve(summary.CaptureSource,
                    true, summary.CustomProperties, summary.CreatedUtc, summary.LastUpdatedUtc),
                IsHistorical = true,
                CacheSizeBytes = cacheSizeBytes,
                IsPinned = summary.IsPinned,
                Author = summary.Author,
                ReplaySource = summary.ReplaySource,
                CaptureSource = summary.CaptureSource,
                SdkVersion = SessionSdkVersion.Resolve(summary.SdkVersion, deviceProfileResult.Profile, deviceProfileResult.ProfileJson),
                Tags = summary.Tags ?? Array.Empty<string>(),
                Notes = summary.Notes,
                CustomProperties = SessionSnapshotCloner.CloneCustomProperties(summary.CustomProperties),
                AppState = summary.AppState,
                AppStateChangedUtc = summary.AppStateChangedUtc,
                DeviceProfile = deviceProfileResult.Profile,
                DeviceProfileJson = deviceProfileResult.ProfileJson,
                AppIcon = appIcon,
                AppToolCatalog = appToolCatalog,
                Analyses = analyses,
                Annotations = annotations,
                AgentTaskLinks = agentTaskLinks,
                Images = images,
                Touches = touches,
                NetworkRequests = networkRequests,
                ApplicationEvents = applicationEvents,
                VisualTreeSnapshots = visualTreeSnapshots,
                ArtifactSnapshots = artifactSnapshots,
                LogStreams = logStreams,
                Logs = logs,
                TotalLogCount = Math.Max(summary.LogCount, logs.Count),
                RetainedLogStartIndex = includeHeavyBlobs ? 0 : Math.Max(0, summary.LogCount),
                MetricChannels = channels,
                Metrics = metrics,
                TotalAnnotationCount = Math.Max(summary.AnnotationCount, annotations.Count),
                TotalImageCount = Math.Max(summary.ImageCount, images.Count),
                TotalMetricChannelCount = Math.Max(summary.MetricChannelCount, channels.Count),
                TotalMetricSampleCount = Math.Max(summary.MetricSampleCount, metrics.Count),
                TotalApplicationEventCount = Math.Max(summary.ApplicationEventCount, applicationEvents.Count),
                TotalNetworkRequestCount = Math.Max(summary.NetworkRequestCount, networkRequests.Count)
            };
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

    private static DeviceProfileLoadResult LoadDeviceProfile(string filePath, CancellationToken cancellationToken = default)
    {
        var document = TryLoadDocument<SessionDeviceProfileBlobDocument>(filePath, cancellationToken);
        var profileJson = NormalizeJson(document?.ProfileJson);
        if (string.IsNullOrWhiteSpace(profileJson))
        {
            return new DeviceProfileLoadResult(null, null);
        }

        try
        {
            return new DeviceProfileLoadResult(
                JsonSerializer.Deserialize<DeviceAppProfile>(profileJson, JsonUtil.Compact),
                profileJson);
        }
        catch
        {
            return new DeviceProfileLoadResult(null, profileJson);
        }
    }

    private static SessionAppToolCatalogSnapshot? LoadAppToolCatalog(
        string filePath,
        CancellationToken cancellationToken = default)
        => SessionSnapshotCloner.CloneAppToolCatalog(
            TryLoadDocument<SessionAppToolCatalogBlobDocument>(filePath, cancellationToken)?.Catalog);

    private static IReadOnlyList<SessionAnalysisRecord> LoadAnalyses(string filePath, CancellationToken cancellationToken = default)
    {
        return TryLoadDocument<SessionAnalysesBlobDocument>(filePath, cancellationToken)?.Analyses ?? Array.Empty<SessionAnalysisRecord>();
    }

    private static IReadOnlyList<SessionAnnotation> LoadAnnotations(string filePath, CancellationToken cancellationToken = default)
    {
        return TryLoadDocument<SessionAnnotationsBlobDocument>(filePath, cancellationToken)?.Annotations ?? Array.Empty<SessionAnnotation>();
    }

    private static IReadOnlyList<SessionApplicationEvent> LoadApplicationEvents(
        string filePath,
        CancellationToken cancellationToken = default)
        => TryLoadDocument<SessionApplicationEventsBlobDocument>(filePath, cancellationToken)?.Events
           ?? Array.Empty<SessionApplicationEvent>();

    internal static IReadOnlyList<SessionNetworkRequest> LoadNetworkRequests(
        string directoryPath,
        IProgress<SessionLoadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ReportLoadProgress(progress, SessionLoadStage.NetworkRequests, 0, "Reading network requests...");
        if (!Directory.Exists(directoryPath))
        {
            ReportLoadProgress(progress, SessionLoadStage.NetworkRequests, 1, "No network requests found.");
            return Array.Empty<SessionNetworkRequest>();
        }

        var requests = new List<SessionNetworkRequest>();
        foreach (var filePath in Directory.EnumerateFiles(directoryPath, "*.json", SearchOption.TopDirectoryOnly)
                     .OrderBy(static path => path, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var request = TryLoadDocument<SessionNetworkRequest>(filePath, cancellationToken);
                var normalized = SessionNetworkRequestSanitizer.Normalize(request);
                if (normalized is not null)
                {
                    requests.Add(normalized);
                }
            }
            catch
            {
                // Ignore malformed individual request records and preserve the rest of the session.
            }
        }

        var result = requests
            .GroupBy(static request => request.Id, StringComparer.Ordinal)
            .Select(static group => group.Last())
            .OrderBy(static request => request.StartedAtUtc)
            .ThenBy(static request => request.Id, StringComparer.Ordinal)
            .ToArray();
        ReportLoadProgress(progress, SessionLoadStage.NetworkRequests, 1, FormatLoadCount(result.Length, "network request", "network requests"));
        return result;
    }

    private static IReadOnlyList<SessionAgentTaskLink> LoadAgentTaskLinks(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        return TryLoadDocument<SessionAgentTaskLinksBlobDocument>(filePath, cancellationToken)?.Tasks
               ?? Array.Empty<SessionAgentTaskLink>();
    }

    internal static IReadOnlyList<SessionImageFrame> LoadImages(
        string filePath,
        string capturedImagesDirectoryPath,
        IProgress<SessionLoadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var baseDocument = TryLoadDocument<SessionImagesBlobDocument>(filePath, cancellationToken);
        var baseImages = baseDocument?.Images ?? Array.Empty<SessionImageFrame>();
        var images = new List<SessionImageFrame>(baseImages.Count);
        images.AddRange(baseImages);
        var appendFilePath = Path.Combine(
            Path.GetDirectoryName(filePath) ?? string.Empty,
            ImagesAppendFileName);
        if (File.Exists(appendFilePath))
        {
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
                        var document = JsonSerializer.Deserialize<SessionImagesAppendDocument>(line, JsonUtil.Compact);
                        if (document?.Images is { Count: > 0 })
                        {
                            images.AddRange(document.Images);
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
                // Preserve the canonical image manifest and any complete appended batches.
            }
        }

        var distinctImages = images
            .Where(frame => !string.IsNullOrWhiteSpace(frame.FrameId))
            .GroupBy(frame => frame.FrameId, StringComparer.Ordinal)
            .Select(group => group.Last())
            .OrderBy(frame => frame.CapturedAtUtc)
            .ThenBy(frame => frame.FrameId, StringComparer.Ordinal)
            .ToArray();
        if (distinctImages.Length == 0)
        {
            return distinctImages;
        }

        var existingImages = new List<SessionImageFrame>(distinctImages.Length);
        for (var index = 0; index < distinctImages.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var frame = distinctImages[index];
            ReportFileLoadProgress(
                progress,
                SessionLoadStage.Images,
                index + 1,
                distinctImages.Length,
                $"Resolving screenshot frame {index + 1:N0} of {distinctImages.Length:N0}...");
            if (File.Exists(SessionImageArtifactPath.ResolveCapturedImagePath(capturedImagesDirectoryPath, frame)))
            {
                existingImages.Add(frame);
            }
        }

        return existingImages.ToArray();
    }

    public static IReadOnlyList<SessionTouchInputRecord> LoadTouches(
        SessionPathLayout layout,
        IProgress<SessionLoadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ReportLoadProgress(progress, SessionLoadStage.Touches, 0, "Reading touch input...");
        var batches = TryLoadDocument<SessionTouchesBlobDocument>(layout.TouchesFilePath, cancellationToken)?.Batches
                      ?? [];
        var appendFilePath = GetTouchesAppendFilePath(layout);
        if (File.Exists(appendFilePath))
        {
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
                        var document = JsonSerializer.Deserialize<SessionTouchesAppendDocument>(line, JsonUtil.Compact);
                        if (document?.Batches is { Count: > 0 })
                        {
                            batches.AddRange(document.Batches);
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
                // Preserve any canonical touch batches that were already loaded.
            }
        }

        var touches = SessionTouchPacking.Unpack(batches)
            .Where(touch => !string.IsNullOrWhiteSpace(touch.Id))
            .GroupBy(touch => touch.Id, StringComparer.Ordinal)
            .Select(group => group.Last())
            .OrderBy(touch => touch.CapturedAtUtc)
            .ThenBy(touch => touch.Id, StringComparer.Ordinal)
            .ToArray();
        ReportLoadProgress(progress, SessionLoadStage.Touches, 1, FormatLoadCount(touches.Length, "touch input record", "touch input records"));
        return touches;
    }

    private static IReadOnlyList<SessionVisualTreeSnapshot> LoadVisualTreeSnapshots(
        string directoryPath,
        IProgress<SessionLoadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ReportLoadProgress(progress, SessionLoadStage.VisualTreeSnapshots, 0, "Reading visual tree snapshots...");
        var snapshots = new List<SessionVisualTreeSnapshot>();
        var filePaths = Directory.Exists(directoryPath)
            ? Directory.EnumerateFiles(directoryPath, "*.json", SearchOption.TopDirectoryOnly)
                .OrderBy(filePath => filePath, StringComparer.Ordinal)
                .ToArray()
            : Array.Empty<string>();
        var totalFileCount = filePaths.Length;
        var processedFileCount = 0;
        if (Directory.Exists(directoryPath))
        {
            foreach (var filePath in filePaths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ReportFileLoadProgress(
                    progress,
                    SessionLoadStage.VisualTreeSnapshots,
                    processedFileCount,
                    totalFileCount,
                    $"Reading visual tree snapshot {processedFileCount + 1:N0} of {totalFileCount:N0}...");
                var document = TryLoadDocument<SessionVisualTreeSnapshotDocument>(filePath, cancellationToken);
                if (document?.Snapshot is not null
                    && string.Equals(document.Schema, SessionVisualTreeSnapshotDocument.SchemaName, StringComparison.Ordinal))
                {
                    snapshots.Add(document.Snapshot);
                    processedFileCount++;
                    ReportFileLoadProgress(
                        progress,
                        SessionLoadStage.VisualTreeSnapshots,
                        processedFileCount,
                        totalFileCount,
                        $"Loaded visual tree snapshot {processedFileCount:N0} of {totalFileCount:N0}.");
                    continue;
                }

                processedFileCount++;
                ReportFileLoadProgress(
                    progress,
                    SessionLoadStage.VisualTreeSnapshots,
                    processedFileCount,
                    totalFileCount,
                    $"Loaded visual tree snapshot {processedFileCount:N0} of {totalFileCount:N0}.");
            }
        }

        var loadedSnapshots = snapshots
            .Where(snapshot => !string.IsNullOrWhiteSpace(snapshot.SnapshotId))
            .GroupBy(snapshot => snapshot.SnapshotId, StringComparer.Ordinal)
            .Select(group => group.OrderByDescending(snapshot => snapshot.CapturedAtUtc).First())
            .OrderBy(snapshot => snapshot.CapturedAtUtc)
            .ThenBy(snapshot => snapshot.SnapshotId, StringComparer.Ordinal)
            .ToArray();
        ReportLoadProgress(progress, SessionLoadStage.VisualTreeSnapshots, 1, FormatLoadCount(loadedSnapshots.Length, "visual tree snapshot", "visual tree snapshots"));
        return loadedSnapshots;
    }

    private static IReadOnlyList<SessionArtifactSnapshot> LoadArtifactSnapshots(
        string directoryPath,
        IProgress<SessionLoadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ReportLoadProgress(progress, SessionLoadStage.ArtifactSnapshots, 0, "Reading artifact snapshots...");
        if (!Directory.Exists(directoryPath))
        {
            ReportLoadProgress(progress, SessionLoadStage.ArtifactSnapshots, 1, "No artifact snapshots found.");
            return Array.Empty<SessionArtifactSnapshot>();
        }

        var filePaths = Directory.EnumerateFiles(directoryPath, "*.json", SearchOption.TopDirectoryOnly)
            .OrderBy(filePath => filePath, StringComparer.Ordinal)
            .ToArray();
        var snapshots = new List<SessionArtifactSnapshot>(filePaths.Length);
        for (var index = 0; index < filePaths.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReportFileLoadProgress(
                progress,
                SessionLoadStage.ArtifactSnapshots,
                index,
                filePaths.Length,
                $"Reading artifact snapshot {index + 1:N0} of {filePaths.Length:N0}...");
            var snapshot = TryLoadDocument<SessionArtifactSnapshotDocument>(filePaths[index], cancellationToken)?.Snapshot
                           ?? TryLoadDocument<SessionArtifactSnapshot>(filePaths[index], cancellationToken);
            if (snapshot is not null)
            {
                snapshots.Add(snapshot);
            }

            ReportFileLoadProgress(
                progress,
                SessionLoadStage.ArtifactSnapshots,
                index + 1,
                filePaths.Length,
                $"Loaded artifact snapshot {index + 1:N0} of {filePaths.Length:N0}.");
        }

        var loadedSnapshots = snapshots
            .Where(snapshot => snapshot is not null
                               && !string.IsNullOrWhiteSpace(snapshot.SnapshotId)
                               && !string.IsNullOrWhiteSpace(snapshot.ArtifactDirectoryName))
            .GroupBy(snapshot => snapshot.SnapshotId, StringComparer.Ordinal)
            .Select(group => group.OrderByDescending(snapshot => snapshot.CapturedAtUtc).First())
            .OrderBy(snapshot => snapshot.CapturedAtUtc)
            .ThenBy(snapshot => snapshot.SnapshotId, StringComparer.Ordinal)
            .ToArray();
        ReportLoadProgress(progress, SessionLoadStage.ArtifactSnapshots, 1, FormatLoadCount(loadedSnapshots.Length, "artifact snapshot", "artifact snapshots"));
        return loadedSnapshots;
    }

    public static SessionAppIcon? LoadAppIcon(string sessionDirectoryPath, SessionAppIcon? appIcon)
    {
        if (appIcon is null)
        {
            return null;
        }

        var appIconPath = SessionAppIconArtifactPath.ResolveSessionIconPath(sessionDirectoryPath, appIcon);
        return File.Exists(appIconPath) ? appIcon : null;
    }

}
