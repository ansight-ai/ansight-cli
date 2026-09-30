using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ansight.Host;
using Ansight.Infrastructure;

namespace Ansight.Host.Runtime.Operations.Tools.Shared;

internal static class StandardSessionArchiveExporter
{
    private const string ArtifactsArchiveDirectoryName = "artifacts";
    private const string SessionLogsArchiveEntryPath = "session-data/logs.json";
    private const string SessionTelemetryArchiveEntryPath = "session-data/telemetry.json";
    private const string SessionTouchesArchiveEntryPath = "session-data/touches.json";

    private static readonly JsonSerializerOptions SessionArchiveJsonOptions = new()
    {
        MaxDepth = JsonUtil.MaximumDepth,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public static OperationResult Export(
        IApplicationPaths applicationPaths,
        AppSessionSnapshot snapshot,
        string archiveFilePath,
        bool includeNativeDeviceLogs = true,
        SessionSanitizationPolicy? sanitizationPolicy = null)
    {
        try
        {
            var directoryPath = Path.GetDirectoryName(archiveFilePath);
            if (string.IsNullOrWhiteSpace(directoryPath))
            {
                return OperationResult.Failure("Choose a valid archive destination.");
            }

            Directory.CreateDirectory(directoryPath);
            if (File.Exists(archiveFilePath))
            {
                File.Delete(archiveFilePath);
            }

            using var stream = File.Create(archiveFilePath);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Create);

            var sessionCapturesRootPath = SessionImageArtifactPath.ResolveSessionCapturesRootPath(applicationPaths);
            var appIcon = snapshot.AppIcon;
            string? sourceAppIconPath = null;
            if (appIcon is not null)
            {
                sourceAppIconPath = SessionAppIconArtifactPath.ResolveSessionIconPath(
                    sessionCapturesRootPath,
                    snapshot.AppId,
                    snapshot.SessionId,
                    appIcon);
                if (!File.Exists(sourceAppIconPath))
                {
                    appIcon = null;
                    sourceAppIconPath = null;
                }
            }

            var exportSnapshot = PrepareSessionSnapshotForExport(
                snapshot,
                appIcon,
                includeNativeDeviceLogs: includeNativeDeviceLogs);
            SessionSanitizationProcessor? sanitizer = null;
            SessionSanitizedCapture? sanitizedCapture = null;
            if (sanitizationPolicy is not null)
            {
                sanitizer = new SessionSanitizationProcessor(sanitizationPolicy);
                sanitizedCapture = sanitizer.Sanitize(exportSnapshot);
                exportSnapshot = sanitizedCapture.Snapshot;
            }

            var exportLogStreams = SessionLogStreams.Normalize(exportSnapshot.LogStreams, exportSnapshot.Logs);
            var sessionDocumentSnapshot = CreateExportSessionDocumentSnapshot(exportSnapshot);
            var savedAtUtc = DateTimeOffset.UtcNow;
            var visualTreeSnapshotIndex = SessionArchiveVisualTreeCodec.Write(
                archive,
                exportSnapshot.VisualTreeSnapshots,
                savedAtUtc,
                SessionArchiveJsonOptions);
            var sessionEntry = archive.CreateEntry("session.json", CompressionLevel.Optimal);
            using (var writer = new StreamWriter(sessionEntry.Open()))
            {
                writer.Write(JsonSerializer.Serialize(
                    new SessionCaptureDocument
                    {
                        SavedAtUtc = savedAtUtc,
                        Author = exportSnapshot.Author,
                        Session = sessionDocumentSnapshot,
                        VisualTreeSnapshotIndex = visualTreeSnapshotIndex
                    },
                    SessionArchiveJsonOptions));
            }

            WriteArchiveJsonEntry(
                archive,
                SessionLogsArchiveEntryPath,
                new SessionArchiveLogsDocument
                {
                    SavedAtUtc = DateTimeOffset.UtcNow,
                    Logs = SessionLogStreamArchiveCodec.GetLegacySdkEntries(exportLogStreams)
                });
            SessionLogStreamArchiveCodec.Write(
                archive,
                exportLogStreams,
                DateTimeOffset.UtcNow,
                SessionArchiveJsonOptions);
            WriteArchiveJsonEntry(
                archive,
                SessionTelemetryArchiveEntryPath,
                new SessionArchiveTelemetryDocument
                {
                    SavedAtUtc = DateTimeOffset.UtcNow,
                    Channels = exportSnapshot.MetricChannels,
                    Metrics = exportSnapshot.Metrics
                });
            WriteArchiveJsonEntry(
                archive,
                SessionTouchesArchiveEntryPath,
                new SessionArchiveTouchesDocument
                {
                    SavedAtUtc = DateTimeOffset.UtcNow,
                    Batches = SessionTouchPacking.Pack(exportSnapshot.Touches)
                },
                JsonUtil.Compact);

            foreach (var request in exportSnapshot.NetworkRequests
                         .OrderBy(static request => request.StartedAtUtc)
                         .ThenBy(static request => request.Id, StringComparer.Ordinal))
            {
                WriteArchiveJsonEntry(
                    archive,
                    $"network/requests/{request.StartedAtUtc:yyyyMMddHHmmssfff}-{FileNameUtil.Sanitize(request.Id)}.json",
                    request);
            }

            if (appIcon is not null && sourceAppIconPath is not null)
            {
                archive.CreateEntryFromFile(
                    sourceAppIconPath,
                    SessionAppIconArtifactPath.ResolveArchiveEntryPath(appIcon),
                    CompressionLevel.NoCompression);
            }

            foreach (var frame in exportSnapshot.Images.OrderBy(frame => frame.CapturedAtUtc))
            {
                var sourceImagePath = SessionImageArtifactPath.ResolveCapturedImagePath(
                    sessionCapturesRootPath,
                    snapshot.AppId,
                    snapshot.SessionId,
                    frame);
                if (!File.Exists(sourceImagePath))
                {
                    continue;
                }

                var entryName = SessionImageArtifactPath.ResolveArchiveEntryPath(frame);
                if (sanitizer is null || sanitizedCapture is null)
                {
                    archive.CreateEntryFromFile(
                        sourceImagePath,
                        entryName,
                        CompressionLevel.NoCompression);
                    continue;
                }

                sanitizer.TryWriteScreenshot(
                    sourceImagePath,
                    frame,
                    archive,
                    entryName,
                    sanitizedCapture.ScreenshotRegionsByFrameId.GetValueOrDefault(frame.FrameId)
                    ?? Array.Empty<SessionSanitizationRegion>());
            }

            var artifactsDirectoryPath = Path.Combine(
                SessionImageArtifactPath.ResolveSessionDirectoryPath(
                    sessionCapturesRootPath,
                    snapshot.AppId,
                    snapshot.SessionId),
                ArtifactsArchiveDirectoryName);
            if (Directory.Exists(artifactsDirectoryPath))
            {
                if (sanitizer is not null)
                {
                    sanitizer.WriteArtifacts(artifactsDirectoryPath, archive, BuildArtifactArchiveEntryPath);
                }
                else
                {
                    foreach (var artifactFilePath in Directory.EnumerateFiles(artifactsDirectoryPath, "*", SearchOption.AllDirectories))
                    {
                        var relativePath = Path.GetRelativePath(artifactsDirectoryPath, artifactFilePath);
                        var entryName = BuildArtifactArchiveEntryPath(relativePath);
                        archive.CreateEntryFromFile(artifactFilePath, entryName, CompressionLevel.NoCompression);
                    }
                }
            }

            if (sanitizedCapture is not null)
            {
                WriteArchiveJsonEntry(
                    archive,
                    "sanitization/report.json",
                    sanitizedCapture.Report.Build());
            }

            var sanitizedMessage = sanitizedCapture is null ? string.Empty : " with sanitization";
            return OperationResult.Success(
                $"Session '{snapshot.SessionId}' exported{sanitizedMessage} to '{archiveFilePath}'.");
        }
        catch (Exception ex)
        {
            return OperationResult.Failure($"Unable to export session archive: {ex.Message}");
        }
    }

    public static SessionImportResult Import(
        IRuntimeState runtimeState,
        string archiveFilePath,
        SessionReplaySource? replaySource = null)
    {
        try
        {
            if (!File.Exists(archiveFilePath))
            {
                return SessionImportResult.Failure($"Archive '{archiveFilePath}' was not found.");
            }

            if (SessionArchiveExternalPayloadReader.IsJsonLinesFilePath(archiveFilePath))
            {
                return SessionArchiveExternalPayloadReader.TryReadJsonLinesFile(
                    archiveFilePath,
                    out var jsonLinesPayload,
                    out var jsonLinesErrorMessage)
                    && jsonLinesPayload is not null
                    ? runtimeState.ImportSessionSnapshot(
                        jsonLinesPayload.Snapshot,
                        jsonLinesPayload.ImageBytesByFrameId,
                        jsonLinesPayload.AppIconBytes,
                        jsonLinesPayload.ArtifactBytesByRelativePath,
                        replaySource)
                    : SessionImportResult.Failure(jsonLinesErrorMessage ?? "The selected JSONL archive could not be read.");
            }

            using var stream = File.OpenRead(archiveFilePath);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
            var sessionEntry = archive.GetEntry("session.json");
            if (sessionEntry is null)
            {
                return SessionArchiveExternalPayloadReader.TryReadJsonLinesArchive(
                    archive,
                    archiveFilePath,
                    out var jsonLinesPayload,
                    out var jsonLinesErrorMessage)
                    && jsonLinesPayload is not null
                    ? runtimeState.ImportSessionSnapshot(
                        jsonLinesPayload.Snapshot,
                        jsonLinesPayload.ImageBytesByFrameId,
                        jsonLinesPayload.AppIconBytes,
                        jsonLinesPayload.ArtifactBytesByRelativePath,
                        replaySource)
                    : SessionImportResult.Failure(jsonLinesErrorMessage ?? "The selected archive is missing session.json.");
            }

            SessionCaptureDocument? document;
            using (var entryStream = sessionEntry.Open())
            {
                document = JsonSerializer.Deserialize<SessionCaptureDocument>(entryStream, SessionArchiveJsonOptions);
            }

            if (document?.Session is null)
            {
                return SessionImportResult.Failure("The selected archive could not be read as an Ansight session export.");
            }

            if (!SessionCaptureDocument.IsSupportedSchema(document.Schema))
            {
                return SessionImportResult.Failure($"Unsupported session archive schema '{document.Schema}'.");
            }

            var imageBytesByFrameId = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            var artifactBytesByRelativePath = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            byte[]? appIconBytes = null;
            var archiveSession = document.Session.Author is null && document.Author is not null
                ? PrepareSessionSnapshotForExport(document.Session, document.Session.AppIcon, document.Author)
                : document.Session;
            archiveSession = SessionArchiveExternalPayloadReader.ApplyExternalArchivePayloads(
                archive,
                archiveSession,
                document.VisualTreeSnapshotIndex);

            if (archiveSession.AppIcon is not null)
            {
                var appIconEntry = archive.GetEntry(SessionAppIconArtifactPath.ResolveArchiveEntryPath(archiveSession.AppIcon));
                if (appIconEntry is null)
                {
                    return SessionImportResult.Failure("The session archive is missing app icon data.");
                }

                using var appIconStream = appIconEntry.Open();
                using var appIconBuffer = new MemoryStream();
                appIconStream.CopyTo(appIconBuffer);
                appIconBytes = appIconBuffer.ToArray();
            }

            foreach (var frame in archiveSession.Images)
            {
                var imageEntry = archive.GetEntry(SessionImageArtifactPath.ResolveArchiveEntryPath(frame));
                if (imageEntry is null)
                {
                    return SessionImportResult.Failure($"The session archive is missing image data for frame '{frame.FrameId}'.");
                }

                using var imageStream = imageEntry.Open();
                using var imageBuffer = new MemoryStream();
                imageStream.CopyTo(imageBuffer);
                imageBytesByFrameId[frame.FrameId] = imageBuffer.ToArray();
            }

            foreach (var entry in archive.Entries)
            {
                if (string.IsNullOrWhiteSpace(entry.Name)
                    || !entry.FullName.StartsWith(ArtifactsArchiveDirectoryName + "/", StringComparison.Ordinal))
                {
                    continue;
                }

                var relativePath = entry.FullName[(ArtifactsArchiveDirectoryName.Length + 1)..];
                if (string.IsNullOrWhiteSpace(relativePath))
                {
                    continue;
                }

                using var artifactStream = entry.Open();
                using var artifactBuffer = new MemoryStream();
                artifactStream.CopyTo(artifactBuffer);
                artifactBytesByRelativePath[relativePath] = artifactBuffer.ToArray();
            }

            return runtimeState.ImportSessionSnapshot(
                archiveSession,
                imageBytesByFrameId,
                appIconBytes,
                artifactBytesByRelativePath,
                replaySource);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or UnauthorizedAccessException)
        {
            return SessionImportResult.Failure($"Unable to import session archive: {ex.Message}");
        }
    }

    internal static AppSessionSnapshot PrepareSessionSnapshotForExport(
        AppSessionSnapshot snapshot,
        SessionAppIcon? appIcon,
        SessionCaptureAuthorMetadata? author = null,
        bool includeNativeDeviceLogs = true)
    {
        var logStreams = SessionArchiveLogSelection.Select(snapshot, includeNativeDeviceLogs);
        var logs = includeNativeDeviceLogs
            ? snapshot.Logs.ToArray()
            : SessionLogStreams.Flatten(logStreams);
        return new AppSessionSnapshot
        {
            SessionId = snapshot.SessionId,
            AppId = snapshot.AppId,
            ClientName = snapshot.ClientName,
            RemoteAddress = snapshot.RemoteAddress,
            CreatedUtc = snapshot.CreatedUtc,
            ConfigId = snapshot.ConfigId,
            ProcessSessionId = snapshot.ProcessSessionId,
            Status = snapshot.Status,
            LastUpdatedUtc = snapshot.LastUpdatedUtc,
            IsHistorical = snapshot.IsHistorical,
            CacheSizeBytes = snapshot.CacheSizeBytes,
            IsPinned = snapshot.IsPinned,
            Author = author ?? snapshot.Author,
            ReplaySource = snapshot.ReplaySource,
            CaptureSource = snapshot.CaptureSource,
            SdkVersion = SessionSdkVersion.Resolve(snapshot.SdkVersion, snapshot.DeviceProfile, snapshot.DeviceProfileJson),
            Name = snapshot.Name,
            Tags = snapshot.Tags.ToArray(),
            Notes = snapshot.Notes,
            CustomProperties = SessionSnapshotCloner.CloneCustomProperties(snapshot.CustomProperties),
            AppState = snapshot.AppState,
            AppStateChangedUtc = snapshot.AppStateChangedUtc,
            DeviceProfile = snapshot.DeviceProfile,
            DeviceProfileJson = snapshot.DeviceProfileJson,
            AppIcon = appIcon,
            AppToolCatalog = SessionSnapshotCloner.CloneAppToolCatalog(snapshot.AppToolCatalog),
            Analyses = snapshot.Analyses.ToArray(),
            Annotations = snapshot.Annotations
                .Select(annotation => new SessionAnnotation
                {
                    AnnotationId = annotation.AnnotationId,
                    StartUtc = annotation.StartUtc,
                    EndUtc = annotation.EndUtc,
                    Label = annotation.Label,
                    Source = annotation.Source,
                    Notes = annotation.Notes,
                    CaptureGroupId = annotation.CaptureGroupId,
                    CustomData = annotation.CustomData?.DeepClone() as JsonObject,
                    Evidence = annotation.Evidence.Select(SessionSnapshotCloner.CloneAnnotationEvidence).ToArray(),
                    HookFailures = annotation.HookFailures.ToArray(),
                    Geometry = annotation.Geometry
                        .Select(geometry => new SessionAnnotationGeometry
                        {
                            GeometryId = geometry.GeometryId,
                            FrameId = geometry.FrameId,
                            CapturedAtUtc = geometry.CapturedAtUtc,
                            Kind = geometry.Kind,
                            X = geometry.X,
                            Y = geometry.Y,
                            Width = geometry.Width,
                            Height = geometry.Height,
                            Points = geometry.Points.Select(SessionSnapshotCloner.CloneAnnotationGeometryPoint).ToArray(),
                            Text = geometry.Text,
                            StrokeColor = geometry.StrokeColor,
                            StrokeWidth = geometry.StrokeWidth
                        })
                        .ToArray(),
                    Target = SessionSnapshotCloner.CloneAnnotationTarget(annotation.Target)
                })
                .ToArray(),
            AgentTaskLinks = snapshot.AgentTaskLinks
                .Select(static taskLink => SessionSnapshotCloner.CloneAgentTaskLink(taskLink))
                .ToArray(),
            Images = snapshot.Images
                .Select(frame => new SessionImageFrame
                {
                    FrameId = frame.FrameId,
                    CapturedAtUtc = frame.CapturedAtUtc,
                    Format = frame.Format,
                    Width = frame.Width,
                    Height = frame.Height,
                    Quality = frame.Quality,
                    ByteCount = frame.ByteCount
                })
                .ToArray(),
            Touches = snapshot.Touches.ToArray(),
            NetworkRequests = snapshot.NetworkRequests.ToArray(),
            VisualTreeSnapshots = snapshot.VisualTreeSnapshots
                .Select(SessionSnapshotCloner.CloneVisualTreeSnapshot)
                .ToArray(),
            ArtifactSnapshots = snapshot.ArtifactSnapshots
                .Select(SessionSnapshotCloner.CloneArtifactSnapshot)
                .ToArray(),
            ApplicationEvents = snapshot.ApplicationEvents.ToArray(),
            LogStreams = logStreams,
            Logs = logs,
            TotalLogCount = SessionArchiveLogSelection.CountTotalEntries(snapshot, logStreams),
            RetainedLogStartIndex = 0,
            MetricChannels = snapshot.MetricChannels.ToArray(),
            Metrics = snapshot.Metrics.ToArray(),
            TotalApplicationEventCount = snapshot.TotalApplicationEventCount,
            TotalNetworkRequestCount = Math.Max(snapshot.TotalNetworkRequestCount, snapshot.NetworkRequests.Count)
        };
    }

    private static AppSessionSnapshot CreateExportSessionDocumentSnapshot(AppSessionSnapshot snapshot)
    {
        return new AppSessionSnapshot
        {
            SessionId = snapshot.SessionId,
            AppId = snapshot.AppId,
            ClientName = snapshot.ClientName,
            RemoteAddress = snapshot.RemoteAddress,
            CreatedUtc = snapshot.CreatedUtc,
            ConfigId = snapshot.ConfigId,
            ProcessSessionId = snapshot.ProcessSessionId,
            Status = snapshot.Status,
            LastUpdatedUtc = snapshot.LastUpdatedUtc,
            IsHistorical = snapshot.IsHistorical,
            CacheSizeBytes = snapshot.CacheSizeBytes,
            IsPinned = snapshot.IsPinned,
            Author = snapshot.Author,
            ReplaySource = snapshot.ReplaySource,
            CaptureSource = snapshot.CaptureSource,
            SdkVersion = snapshot.SdkVersion,
            Name = snapshot.Name,
            Tags = snapshot.Tags,
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
            Touches = Array.Empty<SessionTouchInputRecord>(),
            NetworkRequests = Array.Empty<SessionNetworkRequest>(),
            VisualTreeSnapshots = Array.Empty<SessionVisualTreeSnapshot>(),
            ArtifactSnapshots = snapshot.ArtifactSnapshots,
            ApplicationEvents = snapshot.ApplicationEvents,
            LogStreams = snapshot.LogStreams
                .Select(stream => new SessionLogStream
                {
                    StreamId = stream.StreamId,
                    Kind = stream.Kind,
                    DisplayName = stream.DisplayName,
                    Status = stream.Status,
                    StartedUtc = stream.StartedUtc,
                    EndedUtc = stream.EndedUtc,
                    StatusMessage = stream.StatusMessage,
                    Metadata = stream.Metadata,
                    Entries = Array.Empty<LogEntry>(),
                    TotalEntryCount = Math.Max(stream.TotalEntryCount, stream.Entries.Count),
                    RetainedEntryStartIndex = Math.Max(stream.TotalEntryCount, stream.Entries.Count)
                })
                .ToArray(),
            Logs = Array.Empty<LogEntry>(),
            TotalLogCount = Math.Max(snapshot.TotalLogCount, snapshot.Logs.Count),
            RetainedLogStartIndex = Math.Max(snapshot.TotalLogCount, snapshot.Logs.Count),
            MetricChannels = Array.Empty<SessionMetricChannel>(),
            Metrics = Array.Empty<SessionMetricSample>()
        };
    }

    private static void WriteArchiveJsonEntry<T>(
        ZipArchive archive,
        string entryPath,
        T document,
        JsonSerializerOptions? options = null)
    {
        var entry = archive.CreateEntry(entryPath, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open());
        writer.Write(JsonSerializer.Serialize(document, options ?? SessionArchiveJsonOptions));
    }

    private static string BuildArtifactArchiveEntryPath(string relativePath)
    {
        var normalizedRelativePath = relativePath.Replace('\\', '/').Trim('/');
        return $"{ArtifactsArchiveDirectoryName}/{normalizedRelativePath}";
    }

    private sealed class SessionArchiveLogsDocument
    {
        public string Schema { get; init; } = "ansight.session-archive-logs.v1";
        public required DateTimeOffset SavedAtUtc { get; init; }
        public required IReadOnlyList<LogEntry> Logs { get; init; }
    }

    private sealed class SessionArchiveTelemetryDocument
    {
        public string Schema { get; init; } = "ansight.session-archive-telemetry.v1";
        public required DateTimeOffset SavedAtUtc { get; init; }
        public required IReadOnlyList<SessionMetricChannel> Channels { get; init; }
        public required IReadOnlyList<SessionMetricSample> Metrics { get; init; }
    }

    private sealed class SessionArchiveTouchesDocument
    {
        public string Schema { get; init; } = SessionTouchPacking.SchemaName;
        public required DateTimeOffset SavedAtUtc { get; init; }
        public List<SessionTouchPackedBatch> Batches { get; init; } = [];
    }
}
