using System.IO.Compression;
using System.Text.Json;
using Ansight.Infrastructure;

namespace Ansight.Host.Sessions;

public sealed class SessionArchiveService
{
    private const string ArtifactsArchiveDirectoryName = "artifacts";
    private const string SessionLogsArchiveEntryPath = "session-data/logs.json";
    private const string SessionTelemetryArchiveEntryPath = "session-data/telemetry.json";
    private const string SessionTouchesArchiveEntryPath = "session-data/touches.json";
    private readonly ISessionReader sessionReader;
    private readonly ISessionLifecycle sessionLifecycle;
    private readonly IApplicationPaths applicationPaths;
    private readonly string javaScriptExecutablePath;

    internal SessionArchiveService(
        ISessionReader sessionReader,
        ISessionLifecycle sessionLifecycle,
        IApplicationPaths applicationPaths,
        string javaScriptExecutablePath)
    {
        this.sessionReader = sessionReader ?? throw new ArgumentNullException(nameof(sessionReader));
        this.sessionLifecycle = sessionLifecycle ?? throw new ArgumentNullException(nameof(sessionLifecycle));
        this.applicationPaths = applicationPaths ?? throw new ArgumentNullException(nameof(applicationPaths));
        this.javaScriptExecutablePath = javaScriptExecutablePath;
    }

    public Task<OperationResult> ExportSessionArchiveAsync(
        string sessionId,
        string archiveFilePath,
        CancellationToken cancellationToken = default)
        => ExportSessionArchiveAsync(
            sessionId,
            archiveFilePath,
            includeNativeDeviceLogs: true,
            cancellationToken);

    public Task<OperationResult> ExportSessionArchiveAsync(
        string sessionId,
        string archiveFilePath,
        bool includeNativeDeviceLogs,
        CancellationToken cancellationToken = default)
        => ExportSessionArchiveAsync(
            sessionId,
            archiveFilePath,
            new SessionArchiveExportOptions
            {
                IncludeNativeDeviceLogs = includeNativeDeviceLogs
            },
            cancellationToken);

    public Task<OperationResult> ExportSessionArchiveAsync(
        string sessionId,
        string archiveFilePath,
        SessionArchiveExportOptions options,
        CancellationToken cancellationToken = default)
        => ExportSessionArchiveAsync(sessionId, archiveFilePath, options, report: null, cancellationToken);

    public Task<OperationResult> ExportSessionArchiveAsync(
        string sessionId,
        string archiveFilePath,
        SessionArchiveExportOptions options,
        Action<SessionOptimizationProgress>? report,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(archiveFilePath);
        ArgumentNullException.ThrowIfNull(options);

        var trimmedSessionId = sessionId.Trim();
        var trimmedArchivePath = archiveFilePath.Trim();
        if (!sessionReader.TryGetSessionSnapshotForExport(trimmedSessionId, out var snapshot) || snapshot is null)
        {
            return Task.FromResult(OperationResult.Failure($"Session '{trimmedSessionId}' was not found."));
        }

        return Task.Run(
            () => ExportSessionArchive(snapshot, trimmedArchivePath, options, report),
            cancellationToken);
    }

    public Task<SessionImportResult> ImportSessionArchiveAsync(
        string archiveFilePath,
        CancellationToken cancellationToken = default,
        SessionReplaySource? replaySource = null,
        string? password = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archiveFilePath);

        var trimmedArchivePath = archiveFilePath.Trim();
        return Task.Run(() => ImportSessionArchive(trimmedArchivePath, replaySource, password), cancellationToken);
    }

    internal AppSessionSnapshot SanitizeSessionSnapshot(
        AppSessionSnapshot snapshot,
        string? sanitizerModulePath,
        SessionSanitizerOperationContext? sanitizationContext = null,
        bool includeNativeDeviceLogs = true,
        bool includeNetworkRequests = true,
        bool includeArtifacts = true,
        IReadOnlyList<SessionVisualTreeTypeSelection>? includedVisualTreeTypes = null,
        Action<SessionOptimizationProgress>? report = null)
    {
        var sanitizationSnapshot = PrepareSessionSnapshotForExport(
            snapshot,
            includeNativeDeviceLogs,
            includeNetworkRequests,
            includeArtifacts,
            includedVisualTreeTypes);
        var sessionCapturesRootPath = SessionImageArtifactPath.ResolveSessionCapturesRootPath(applicationPaths);
        using ISessionArchiveSanitizer sanitizer = string.IsNullOrWhiteSpace(sanitizerModulePath)
            ? new SessionSanitizationProcessor(SessionSanitizationPolicy.PiiSafeDefault, report)
            : new ScriptedSessionSanitizationProcessor(
                sanitizerModulePath,
                javaScriptExecutablePath,
                sessionCapturesRootPath,
                snapshot.AppId,
                snapshot.SessionId,
                operationContext: sanitizationContext,
                report: report);
        report?.Invoke(new SessionOptimizationProgress("Sanitizing upload metadata…"));
        return sanitizer.Sanitize(sanitizationSnapshot).Snapshot;
    }

    internal static AppSessionSnapshot PrepareSessionSnapshotForExport(
        AppSessionSnapshot snapshot,
        bool includeNativeDeviceLogs,
        bool includeNetworkRequests = true,
        bool includeArtifacts = true,
        IReadOnlyList<SessionVisualTreeTypeSelection>? includedVisualTreeTypes = null)
        => CreateExportSnapshot(
            snapshot,
            snapshot.AppIcon,
            includeNativeDeviceLogs: includeNativeDeviceLogs,
            includeNetworkRequests: includeNetworkRequests,
            includeArtifacts: includeArtifacts,
            includedVisualTreeTypes: includedVisualTreeTypes);

    private OperationResult ExportSessionArchive(
        AppSessionSnapshot snapshot,
        string archiveFilePath,
        SessionArchiveExportOptions options,
        Action<SessionOptimizationProgress>? report)
    {
        ISessionArchiveSanitizer? sanitizer = null;
        try
        {
            var directoryPath = Path.GetDirectoryName(archiveFilePath);
            if (string.IsNullOrWhiteSpace(directoryPath))
            {
                return OperationResult.Failure("Choose a valid archive destination.");
            }

            Directory.CreateDirectory(directoryPath);
            report?.Invoke(new SessionOptimizationProgress("Preparing archive contents…"));

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

            var exportSnapshot = CreateExportSnapshot(
                snapshot,
                appIcon,
                includeNativeDeviceLogs: options.IncludeNativeDeviceLogs,
                includeNetworkRequests: options.IncludeNetworkRequests,
                includeArtifacts: options.IncludeArtifacts,
                includedVisualTreeTypes: options.IncludedVisualTreeTypes);
            var sourceArtifactSnapshots = exportSnapshot.ArtifactSnapshots;
            SessionSanitizedCapture? sanitizedCapture = null;
            if (!string.IsNullOrWhiteSpace(options.SanitizerModulePath))
            {
                sanitizer = new ScriptedSessionSanitizationProcessor(
                    options.SanitizerModulePath,
                    javaScriptExecutablePath,
                    sessionCapturesRootPath,
                    snapshot.AppId,
                    snapshot.SessionId,
                    operationContext: options.SanitizationContext,
                    report: report);
                report?.Invoke(new SessionOptimizationProgress("Sanitizing session evidence…"));
                sanitizedCapture = sanitizer.Sanitize(exportSnapshot);
                exportSnapshot = sanitizedCapture.Snapshot;
            }
            else if (options.Sanitize)
            {
                sanitizer = new SessionSanitizationProcessor(SessionSanitizationPolicy.PiiSafeDefault, report);
                report?.Invoke(new SessionOptimizationProgress("Applying the built-in privacy policy…"));
                sanitizedCapture = sanitizer.Sanitize(exportSnapshot);
                exportSnapshot = sanitizedCapture.Snapshot;
            }

            var artifactsDirectoryPath = Path.Combine(
                SessionImageArtifactPath.ResolveSessionDirectoryPath(
                    sessionCapturesRootPath,
                    snapshot.AppId,
                    snapshot.SessionId),
                ArtifactsArchiveDirectoryName);
            var artifactsWritten = false;
            if (options.IncludeArtifacts && sanitizer is not null && Directory.Exists(artifactsDirectoryPath))
            {
                report?.Invoke(new SessionOptimizationProgress("Sanitizing captured artifacts…"));
                var writtenArtifactPaths = sanitizer.WriteArtifacts(
                    artifactsDirectoryPath,
                    archive,
                    BuildArtifactArchiveEntryPath,
                    BuildSanitizedArtifactPathMap(sourceArtifactSnapshots, exportSnapshot.ArtifactSnapshots));
                exportSnapshot = CloneWithIncludedArtifactFiles(exportSnapshot, writtenArtifactPaths);
                artifactsWritten = true;
            }

            report?.Invoke(new SessionOptimizationProgress("Writing session data…"));
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

            var networkRequests = exportSnapshot.NetworkRequests
                         .OrderBy(static request => request.StartedAtUtc)
                         .ThenBy(static request => request.Id, StringComparer.Ordinal)
                         .ToArray();
            for (var requestIndex = 0; requestIndex < networkRequests.Length; requestIndex++)
            {
                var request = networkRequests[requestIndex];
                WriteArchiveJsonEntry(
                    archive,
                    $"network/requests/{request.StartedAtUtc:yyyyMMddHHmmssfff}-{FileNameUtil.Sanitize(request.Id)}.json",
                    request);
                report?.Invoke(new SessionOptimizationProgress(
                    "Writing network traffic…",
                    requestIndex + 1,
                    networkRequests.Length));
            }

            if (appIcon is not null && sourceAppIconPath is not null)
            {
                var appIconEntry = archive.CreateEntry(
                    SessionAppIconArtifactPath.ResolveArchiveEntryPath(appIcon),
                    CompressionLevel.NoCompression);
                using var input = File.OpenRead(sourceAppIconPath);
                using var output = appIconEntry.Open();
                input.CopyTo(output);
            }

            var imageFrames = exportSnapshot.Images.OrderBy(frame => frame.CapturedAtUtc).ToArray();
            for (var imageIndex = 0; imageIndex < imageFrames.Length; imageIndex++)
            {
                var frame = imageFrames[imageIndex];
                var sourceImagePath = SessionImageArtifactPath.ResolveCapturedImagePath(
                    sessionCapturesRootPath,
                    snapshot.AppId,
                    snapshot.SessionId,
                    frame);
                if (!File.Exists(sourceImagePath))
                {
                    continue;
                }

                var entryName = BuildImageEntryPath(frame);
                if (sanitizer is null || sanitizedCapture is null)
                {
                    var imageEntry = archive.CreateEntry(entryName, CompressionLevel.NoCompression);
                    using var input = File.OpenRead(sourceImagePath);
                    using var output = imageEntry.Open();
                    input.CopyTo(output);
                    report?.Invoke(new SessionOptimizationProgress(
                        "Writing screenshots…",
                        imageIndex + 1,
                        imageFrames.Length));
                    continue;
                }

                sanitizer.TryWriteScreenshot(
                    sourceImagePath,
                    frame,
                    archive,
                    entryName,
                    sanitizedCapture.ScreenshotRegionsByFrameId.GetValueOrDefault(frame.FrameId)
                    ?? Array.Empty<SessionSanitizationRegion>());
                report?.Invoke(new SessionOptimizationProgress(
                    "Writing screenshots…",
                    imageIndex + 1,
                    imageFrames.Length));
            }

            if (options.IncludeArtifacts && !artifactsWritten && Directory.Exists(artifactsDirectoryPath))
            {
                report?.Invoke(new SessionOptimizationProgress("Writing captured artifacts…"));
                var artifactFilePaths = Directory.EnumerateFiles(artifactsDirectoryPath, "*", SearchOption.AllDirectories).ToArray();
                for (var artifactIndex = 0; artifactIndex < artifactFilePaths.Length; artifactIndex++)
                {
                    var artifactFilePath = artifactFilePaths[artifactIndex];
                    var relativePath = Path.GetRelativePath(artifactsDirectoryPath, artifactFilePath);
                    var entryName = BuildArtifactArchiveEntryPath(relativePath);
                    var artifactEntry = archive.CreateEntry(entryName, CompressionLevel.NoCompression);
                    using var input = File.OpenRead(artifactFilePath);
                    using var output = artifactEntry.Open();
                    input.CopyTo(output);
                    report?.Invoke(new SessionOptimizationProgress(
                        "Writing captured artifacts…",
                        artifactIndex + 1,
                        artifactFilePaths.Length));
                }
            }

            if (sanitizedCapture is not null && options.IncludeSanitizationReport)
            {
                WriteArchiveJsonEntry(
                    archive,
                    "sanitization/report.json",
                    sanitizedCapture.Report.Build());
            }

            report?.Invoke(new SessionOptimizationProgress("Finalizing the session archive…", 1, 1));
            var sanitizedMessage = sanitizedCapture is null ? string.Empty : " with sanitization";
            return OperationResult.Success(
                $"Session '{snapshot.SessionId}' exported{sanitizedMessage} to '{archiveFilePath}'.");
        }
        catch (Exception ex)
        {
            try
            {
                if (File.Exists(archiveFilePath))
                {
                    File.Delete(archiveFilePath);
                }
            }
            catch (IOException)
            {
                // Preserve the sanitizer failure as the primary result.
            }
            catch (UnauthorizedAccessException)
            {
                // Preserve the sanitizer failure as the primary result.
            }

            return OperationResult.Failure($"Unable to export session archive: {ex.Message}");
        }
        finally
        {
            sanitizer?.Dispose();
        }
    }

    private SessionImportResult ImportSessionArchive(
        string archiveFilePath,
        SessionReplaySource? replaySource,
        string? password)
    {
        var hasEncryptedEntries = false;
        try
        {
            if (!File.Exists(archiveFilePath))
            {
                return SessionImportResult.Failure(
                    $"Archive '{archiveFilePath}' was not found.",
                    SessionImportFailureReason.IoError);
            }

            if (SessionArchiveExternalPayloadReader.IsJsonLinesFilePath(archiveFilePath))
            {
                return SessionArchiveExternalPayloadReader.TryReadJsonLinesFile(
                    archiveFilePath,
                    out var jsonLinesPayload,
                    out var jsonLinesErrorMessage)
                    && jsonLinesPayload is not null
                    ? sessionLifecycle.ImportSessionSnapshot(
                        jsonLinesPayload.Snapshot,
                        jsonLinesPayload.ImageBytesByFrameId,
                        jsonLinesPayload.AppIconBytes,
                        jsonLinesPayload.ArtifactBytesByRelativePath,
                        replaySource)
                    : SessionImportResult.Failure(jsonLinesErrorMessage ?? "The selected JSONL archive could not be read.");
            }

            using var archive = new SharpZipSessionArchiveReader(archiveFilePath, password);
            hasEncryptedEntries = archive.HasEncryptedEntries;
            if (hasEncryptedEntries && string.IsNullOrEmpty(password))
            {
                return SessionImportResult.Failure(
                    "This recording is password protected.",
                    SessionImportFailureReason.PasswordRequired);
            }

            var sessionEntry = archive.GetEntry("session.json");
            if (sessionEntry is null)
            {
                return SessionArchiveExternalPayloadReader.TryReadJsonLinesArchive(
                    archive,
                    archiveFilePath,
                    out var jsonLinesPayload,
                    out var jsonLinesErrorMessage)
                    && jsonLinesPayload is not null
                    ? sessionLifecycle.ImportSessionSnapshot(
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
                ? CreateExportSnapshot(document.Session, document.Session.AppIcon, document.Author)
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
                var imageEntry = archive.GetEntry(BuildImageEntryPath(frame));
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

            return sessionLifecycle.ImportSessionSnapshot(
                archiveSession,
                imageBytesByFrameId,
                appIconBytes,
                artifactBytesByRelativePath,
                replaySource);
        }
        catch (ICSharpCode.SharpZipLib.Zip.ZipException) when (hasEncryptedEntries)
        {
            return SessionImportResult.Failure(
                "The recording password is incorrect, or the encrypted archive is damaged.",
                SessionImportFailureReason.InvalidPassword);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or UnauthorizedAccessException)
        {
            return SessionImportResult.Failure(
                $"Unable to import session archive: {ex.Message}",
                ex is IOException or UnauthorizedAccessException
                    ? SessionImportFailureReason.IoError
                    : SessionImportFailureReason.InvalidArchive);
        }
    }

    private static AppSessionSnapshot CreateExportSnapshot(
        AppSessionSnapshot snapshot,
        SessionAppIcon? appIcon,
        SessionCaptureAuthorMetadata? author = null,
        bool includeNativeDeviceLogs = true,
        bool includeNetworkRequests = true,
        bool includeArtifacts = true,
        IReadOnlyList<SessionVisualTreeTypeSelection>? includedVisualTreeTypes = null)
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
                    CustomData = annotation.CustomData?.DeepClone() as System.Text.Json.Nodes.JsonObject,
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
            NetworkRequests = includeNetworkRequests ? snapshot.NetworkRequests.ToArray() : [],
            VisualTreeSnapshots = snapshot.VisualTreeSnapshots
                .Where(item => includedVisualTreeTypes is null
                               || includedVisualTreeTypes.Any(selection => selection.Matches(item)))
                .Select(SessionSnapshotCloner.CloneVisualTreeSnapshot)
                .ToArray(),
            ArtifactSnapshots = (includeArtifacts ? snapshot.ArtifactSnapshots : [])
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
            TotalNetworkRequestCount = includeNetworkRequests
                ? Math.Max(snapshot.TotalNetworkRequestCount, snapshot.NetworkRequests.Count)
                : 0
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
            Metrics = Array.Empty<SessionMetricSample>(),
            TotalNetworkRequestCount = Math.Max(snapshot.TotalNetworkRequestCount, snapshot.NetworkRequests.Count)
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

    private static string BuildImageEntryPath(SessionImageFrame frame)
    {
        return SessionImageArtifactPath.ResolveArchiveEntryPath(frame);
    }

    private static string BuildArtifactArchiveEntryPath(string relativePath)
    {
        var normalizedRelativePath = relativePath.Replace('\\', '/').Trim('/');
        return $"{ArtifactsArchiveDirectoryName}/{normalizedRelativePath}";
    }

    private static AppSessionSnapshot CloneWithIncludedArtifactFiles(
        AppSessionSnapshot snapshot,
        IReadOnlySet<string> includedArtifactPaths)
    {
        var artifactSnapshots = snapshot.ArtifactSnapshots
            .Select(artifactSnapshot =>
            {
                var entries = artifactSnapshot.Entries
                    .Where(entry => includedArtifactPaths.Contains(NormalizeArtifactRelativePath(
                        $"{artifactSnapshot.ArtifactDirectoryName}/{entry.ArchiveRelativePath}")))
                    .Select(SessionSnapshotCloner.CloneArtifactEntry)
                    .ToArray();
                if (entries.Length == 0)
                {
                    return null;
                }

                return new SessionArtifactSnapshot
                {
                    SnapshotId = artifactSnapshot.SnapshotId,
                    CapturedAtUtc = artifactSnapshot.CapturedAtUtc,
                    Source = artifactSnapshot.Source,
                    RootAlias = artifactSnapshot.RootAlias,
                    RootPath = artifactSnapshot.RootPath,
                    RelativePath = artifactSnapshot.RelativePath,
                    Name = artifactSnapshot.Name,
                    Kind = artifactSnapshot.Kind,
                    ArtifactDirectoryName = artifactSnapshot.ArtifactDirectoryName,
                    DirectoryCount = entries.Count(static entry => string.Equals(entry.Kind, "directory", StringComparison.OrdinalIgnoreCase)),
                    FileCount = entries.Count(static entry => !string.Equals(entry.Kind, "directory", StringComparison.OrdinalIgnoreCase)),
                    ByteCount = entries.Sum(static entry => entry.SizeBytes),
                    Truncated = artifactSnapshot.Truncated,
                    Entries = entries
                };
            })
            .Where(static artifactSnapshot => artifactSnapshot is not null)
            .Cast<SessionArtifactSnapshot>()
            .ToArray();
        var node = JsonSerializer.SerializeToNode(snapshot, SessionArchiveJsonOptions)?.AsObject()
                   ?? throw new InvalidDataException("The sanitized session snapshot could not be updated.");
        node["artifactSnapshots"] = JsonSerializer.SerializeToNode(artifactSnapshots, SessionArchiveJsonOptions);
        return node.Deserialize<AppSessionSnapshot>(SessionArchiveJsonOptions)
               ?? throw new InvalidDataException("The sanitized session snapshot could not be reconstructed.");
    }

    private static string NormalizeArtifactRelativePath(string path)
        => path.Replace('\\', '/').Trim('/');

    private static IReadOnlyDictionary<string, string> BuildSanitizedArtifactPathMap(
        IReadOnlyList<SessionArtifactSnapshot> sourceSnapshots,
        IReadOnlyList<SessionArtifactSnapshot> sanitizedSnapshots)
    {
        var sanitizedById = sanitizedSnapshots
            .Where(static snapshot => !string.IsNullOrWhiteSpace(snapshot.SnapshotId))
            .GroupBy(static snapshot => snapshot.SnapshotId, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.First(), StringComparer.Ordinal);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var snapshotIndex = 0; snapshotIndex < sourceSnapshots.Count; snapshotIndex++)
        {
            var sourceSnapshot = sourceSnapshots[snapshotIndex];
            if (!sanitizedById.TryGetValue(sourceSnapshot.SnapshotId, out var sanitizedSnapshot))
            {
                if (sourceSnapshots.Count != sanitizedSnapshots.Count)
                {
                    continue;
                }

                sanitizedSnapshot = sanitizedSnapshots[snapshotIndex];
            }

            if (sourceSnapshot.Entries.Count != sanitizedSnapshot.Entries.Count)
            {
                continue;
            }

            for (var entryIndex = 0; entryIndex < sourceSnapshot.Entries.Count; entryIndex++)
            {
                var sourceEntry = sourceSnapshot.Entries[entryIndex];
                var sanitizedEntry = sanitizedSnapshot.Entries[entryIndex];
                var sourcePath = NormalizeArtifactRelativePath(
                    $"{sourceSnapshot.ArtifactDirectoryName}/{sourceEntry.ArchiveRelativePath}");
                var sanitizedPath = NormalizeArtifactRelativePath(
                    $"{sanitizedSnapshot.ArtifactDirectoryName}/{sanitizedEntry.ArchiveRelativePath}");
                result[sourcePath] = sanitizedPath;
            }
        }

        return result;
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

    private static readonly JsonSerializerOptions SessionArchiveJsonOptions = new()
    {
        MaxDepth = JsonUtil.MaximumDepth,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };
}
