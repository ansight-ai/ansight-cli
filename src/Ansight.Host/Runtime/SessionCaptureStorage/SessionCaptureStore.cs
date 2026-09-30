namespace Ansight.Host.Runtime.SessionCaptureStorage;

using AppLifecycleState = global::Ansight.AppLifecycleState;
using Ansight.Host;
using Ansight.Pairing.Models;
using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Threading.Channels;
using static SessionCaptureFileSystem;

[Export]
internal sealed partial class SessionCaptureStore
{
    private const string SessionSummaryFileName = "session.json";
    private const string SessionCacheArchiveSuffix = ".session-cache.zip";
    private const string SessionCacheExpansionDirectoryMarker = ".session-cache-expand-";
    private const string SessionCacheCompactionResetFileName = ".session-cache-compaction-reset";
    private const int MaxPendingImageWriteQueueLength = 4;
    private const int PersistenceWriterCount = 2;
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
    private static readonly TimeSpan LogPersistenceBatchInterval = TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan IndexRefreshInterval = TimeSpan.FromSeconds(30);
    private static readonly Ansight.Infrastructure.Logging.ILogger log = Ansight.Infrastructure.Logging.Logger.Create();
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

    private readonly string capturesRootPath;
    private readonly ProductAnalytics analytics;
    private readonly Lock indexGate = new();
    private readonly Lock pendingSnapshotGate = new();
    private readonly Lock pendingLogBatchGate = new();
    private readonly Lock sessionPersistenceLocksGate = new();
    private readonly Lock artifactGate = new();
    private readonly Dictionary<string, SessionPersistenceLockState> persistenceLockStateBySessionId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AppSessionSnapshot> pendingSnapshotsBySessionId = new(StringComparer.Ordinal);
    private readonly HashSet<string> queuedSnapshotSessionIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> deletedSessionIds = new(StringComparer.Ordinal);
    private readonly Dictionary<SessionLogPersistenceKey, SessionLogPersistenceBatch> pendingLogBatches = [];
    private readonly HashSet<SessionLogPersistenceKey> queuedLogBatchKeys = [];
    private readonly ConcurrentDictionary<SessionLogPersistenceKey, int> persistedLogCountByStream = [];
    private readonly ConcurrentDictionary<string, PersistedSessionStatus> persistedStatusBySessionId = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SessionIncrementalPersistenceState> incrementalPersistenceStateBySessionId = new(StringComparer.Ordinal);
    private readonly Channel<string> pendingSnapshotQueue = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
    {
        SingleReader = false
    });
    private readonly Channel<SessionLogPersistenceKey> pendingLogBatchQueue = Channel.CreateUnbounded<SessionLogPersistenceKey>(new UnboundedChannelOptions
    {
        SingleReader = false
    });
    private readonly Channel<PendingImageWrite> pendingImageQueue = Channel.CreateBounded<PendingImageWrite>(new BoundedChannelOptions(MaxPendingImageWriteQueueLength)
    {
        SingleReader = true,
        FullMode = BoundedChannelFullMode.Wait
    });
    private readonly Dictionary<string, SessionImageArtifact> lastArtifactBySessionId = new(StringComparer.Ordinal);
    private readonly Task snapshotWriterTask;
    private readonly Task logBatchWriterTask;
    private readonly Task imageWriterTask;
    private Dictionary<string, string>? filePathBySessionId;
    private Dictionary<string, string>? archivePathBySessionId;
    private Dictionary<string, long>? cacheSizeBytesBySessionId;
    private Dictionary<string, SessionCaptureSummary>? summaryBySessionId;
    private Dictionary<string, AppSessionSnapshot>? summarySnapshotBySessionId;
    private DateTimeOffset lastIndexRefreshUtc = DateTimeOffset.MinValue;
    private DateTime lastObservedCapturesWriteUtc = DateTime.MinValue;

    internal string CapturesRootPath => capturesRootPath;

    internal bool IsSessionIdRetired(string sessionId)
        => !string.IsNullOrWhiteSpace(sessionId) && IsDeleted(sessionId.Trim());

    [ImportingConstructor]
    public SessionCaptureStore(IApplicationPaths applicationPaths)
    {
        ArgumentNullException.ThrowIfNull(applicationPaths);

        analytics = ProductAnalytics.For(applicationPaths);
        capturesRootPath = Path.Combine(applicationPaths.ApplicationDataPath, "session-captures");
        Directory.CreateDirectory(capturesRootPath);
        snapshotWriterTask = Task.WhenAll(Enumerable.Range(0, PersistenceWriterCount)
            .Select(_ => Task.Run(ProcessPendingSnapshotsAsync)));
        logBatchWriterTask = Task.WhenAll(Enumerable.Range(0, PersistenceWriterCount)
            .Select(_ => Task.Run(ProcessPendingLogBatchesAsync)));
        imageWriterTask = Task.Run(ProcessPendingImagesAsync);
    }

    public IReadOnlyList<AppSessionSnapshot> LoadSummaries()
    {
        lock (indexGate)
        {
            RefreshIndexIfStale();
            return summarySnapshotBySessionId?.Values
                .OrderByDescending(snapshot => snapshot.LastUpdatedUtc)
                .ToArray() ?? Array.Empty<AppSessionSnapshot>();
        }
    }

    public bool TryLoad(string sessionId, out AppSessionSnapshot? snapshot)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            snapshot = null;
            return false;
        }

        if (!TryGetSummaryFilePath(sessionId, reloadIndex: true, out var filePath))
        {
            snapshot = null;
            return false;
        }

        snapshot = SessionSnapshotReader.LoadFullSnapshot(filePath);
        return snapshot is not null;
    }

    public AppSessionSnapshot ExpandRetainedLogs(AppSessionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.TotalLogCount <= snapshot.Logs.Count)
        {
            return snapshot;
        }

        return RunWithSessionPersistenceLock(snapshot.SessionId, () =>
        {
            foreach (var batch in TakePendingLogBatches(snapshot.SessionId))
            {
                PersistLogBatchCore(batch);
            }

            var layout = GetSessionLayout(snapshot.AppId, snapshot.SessionId);
            var logs = SessionPersistenceContentMerger.BuildCompactedLogs(layout, snapshot);
            var descriptors = snapshot.LogStreams.Select(stream => new SessionLogStream
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
                TotalEntryCount = stream.TotalEntryCount,
                RetainedEntryStartIndex = stream.TotalEntryCount
            }).ToArray();
            var streams = SessionLogStreams.Normalize(descriptors, logs);
            return SessionSnapshotCloner.WithLogs(snapshot, streams, logs);
        });
    }

    public Task<AppSessionSnapshot?> LoadAsync(
        string sessionId,
        IProgress<SessionLoadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return Task.FromResult<AppSessionSnapshot?>(null);
        }

        var trimmedSessionId = sessionId.Trim();
        return Task.Run(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!TryGetSummaryFilePath(trimmedSessionId, reloadIndex: true, out var filePath))
                {
                    return null;
                }

                return SessionSnapshotReader.LoadFullSnapshot(filePath, progress, cancellationToken);
            },
            cancellationToken);
    }

    public int GetHighestSessionNumber()
    {
        lock (indexGate)
        {
            RefreshIndexIfStale();
            return summaryBySessionId?.Keys
                .Select(RuntimeState.ParseSessionNumber)
                .DefaultIfEmpty(0)
                .Max() ?? 0;
        }
    }

    public bool TryFindSessionId(string appId, string processSessionId, out string sessionId)
    {
        sessionId = string.Empty;
        if (string.IsNullOrWhiteSpace(appId) || string.IsNullOrWhiteSpace(processSessionId))
        {
            return false;
        }

        var normalizedAppId = appId.Trim();
        var normalizedProcessSessionId = processSessionId.Trim();

        lock (indexGate)
        {
            RefreshIndexIfStale();
            if (summaryBySessionId is null || summaryBySessionId.Count == 0)
            {
                return false;
            }

            var match = summaryBySessionId.Values
                .Where(summary => string.Equals(summary.AppId, normalizedAppId, StringComparison.Ordinal)
                                  && string.Equals(summary.ProcessSessionId, normalizedProcessSessionId, StringComparison.Ordinal))
                .OrderByDescending(summary => summary.LastUpdatedUtc)
                .FirstOrDefault();
            if (match is null)
            {
                return false;
            }

            sessionId = match.SessionId;
            return true;
        }
    }

    public bool TryGetSessionCacheSizeBytes(string sessionId, out long cacheSizeBytes)
    {
        cacheSizeBytes = 0;
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return false;
        }

        var trimmedSessionId = sessionId.Trim();
        string? summaryFilePath;
        string? archiveFilePath;
        lock (indexGate)
        {
            EnsureIndex();
            if (cacheSizeBytesBySessionId is not null
                && cacheSizeBytesBySessionId.TryGetValue(trimmedSessionId, out cacheSizeBytes))
            {
                return true;
            }

            filePathBySessionId!.TryGetValue(trimmedSessionId, out summaryFilePath);
            archivePathBySessionId!.TryGetValue(trimmedSessionId, out archiveFilePath);
            if (string.IsNullOrWhiteSpace(summaryFilePath)
                && string.IsNullOrWhiteSpace(archiveFilePath))
            {
                return false;
            }
        }

        if (!string.IsNullOrWhiteSpace(archiveFilePath))
        {
            cacheSizeBytes = GetFileSize(archiveFilePath);
        }
        else
        {
            var layout = SessionSnapshotReader.GetSessionLayoutFromSummaryPath(summaryFilePath!);
            cacheSizeBytes = GetDirectorySize(layout.SessionDirectoryPath);
        }
        lock (indexGate)
        {
            EnsureIndex();
            cacheSizeBytesBySessionId![trimmedSessionId] = cacheSizeBytes;
        }

        return true;
    }

    public bool ExpandSessionCache(string sessionId)
        => TryGetSummaryFilePath(sessionId, reloadIndex: true, out _);

    public void Save(AppSessionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        PersistSnapshot(snapshot);
    }

    public void SaveImportedSnapshot(
        AppSessionSnapshot snapshot,
        IReadOnlyDictionary<string, byte[]> imageBytesByFrameId,
        byte[]? appIconBytes = null,
        IReadOnlyDictionary<string, byte[]>? artifactBytesByRelativePath = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(imageBytesByFrameId);

        var snapshotCopy = SessionSnapshotReader.CloneAsHistorical(snapshot);
        var layout = GetSessionLayout(snapshotCopy.AppId, snapshotCopy.SessionId);

        RunWithSessionPersistenceLock(snapshotCopy.SessionId, () =>
        {
            AllowPersistence(snapshotCopy.SessionId);

            try
            {
                if (Directory.Exists(layout.SessionDirectoryPath))
                {
                    Directory.Delete(layout.SessionDirectoryPath, recursive: true);
                }

                DeleteIfExists(SessionCaptureArchiveCodec.GetSessionArchivePath(layout.SessionDirectoryPath));

                Directory.CreateDirectory(layout.CapturedImagesDirectoryPath);
                if (snapshotCopy.AppIcon is not null)
                {
                    if (appIconBytes is null || appIconBytes.Length == 0)
                    {
                        throw new InvalidDataException("The session archive is missing app icon data.");
                    }

                    var appIconPath = SessionAppIconArtifactPath.ResolveSessionIconPath(
                        layout.SessionDirectoryPath,
                        snapshotCopy.AppIcon);
                    WriteAllBytesAtomic(appIconPath, appIconBytes);
                }

                foreach (var frame in snapshotCopy.Images)
                {
                    if (!imageBytesByFrameId.TryGetValue(frame.FrameId, out var bytes) || bytes.Length == 0)
                    {
                        throw new InvalidDataException($"The session archive is missing image data for frame '{frame.FrameId}'.");
                    }

                    var imagePath = SessionImageArtifactPath.ResolveCapturedImagePath(layout.CapturedImagesDirectoryPath, frame);
                    WriteAllBytesAtomic(imagePath, bytes);
                }

                WriteImportedArtifactFiles(layout, artifactBytesByRelativePath);

                SaveSnapshot(snapshotCopy);
            }
            catch
            {
                TryDeleteDirectory(layout.SessionDirectoryPath);

                lock (indexGate)
                {
                    ReloadIndex();
                    filePathBySessionId?.Remove(snapshotCopy.SessionId);
                    archivePathBySessionId?.Remove(snapshotCopy.SessionId);
                    cacheSizeBytesBySessionId?.Remove(snapshotCopy.SessionId);
                }

                throw;
            }
        });
    }

    public void SaveExtractedSnapshot(AppSessionSnapshot snapshot, AppSessionSnapshot sourceSnapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(sourceSnapshot);

        var snapshotCopy = SessionSnapshotReader.CloneAsHistorical(snapshot);
        var targetLayout = GetSessionLayout(snapshotCopy.AppId, snapshotCopy.SessionId);
        var sourceLayout = GetSessionLayout(sourceSnapshot.AppId, sourceSnapshot.SessionId);

        RunWithSessionPersistenceLocks([snapshotCopy.SessionId, sourceSnapshot.SessionId], () =>
        {
            AllowPersistence(snapshotCopy.SessionId);

            try
            {
                if (Directory.Exists(targetLayout.SessionDirectoryPath))
                {
                    Directory.Delete(targetLayout.SessionDirectoryPath, recursive: true);
                }

                DeleteIfExists(SessionCaptureArchiveCodec.GetSessionArchivePath(targetLayout.SessionDirectoryPath));

                Directory.CreateDirectory(targetLayout.CapturedImagesDirectoryPath);
                CopyExtractedAppIcon(sourceLayout, targetLayout, sourceSnapshot.AppIcon, snapshotCopy.AppIcon);
                CopyExtractedCapturedImages(sourceLayout, targetLayout, snapshotCopy.Images);
                CopyExtractedArtifactPayloadDirectories(sourceLayout, targetLayout, snapshotCopy.ArtifactSnapshots);

                SaveSnapshot(snapshotCopy);
            }
            catch
            {
                TryDeleteDirectory(targetLayout.SessionDirectoryPath);

                lock (indexGate)
                {
                    ReloadIndex();
                    filePathBySessionId?.Remove(snapshotCopy.SessionId);
                    archivePathBySessionId?.Remove(snapshotCopy.SessionId);
                    cacheSizeBytesBySessionId?.Remove(snapshotCopy.SessionId);
                }

                throw;
            }
        });
    }

    public void SaveSessionArtifactSnapshotFiles(
        string appId,
        string sessionId,
        SessionArtifactSnapshot snapshot,
        string sourceDirectoryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDirectoryPath);

        if (string.IsNullOrWhiteSpace(snapshot.ArtifactDirectoryName))
        {
            throw new InvalidDataException("The artifact snapshot is missing a directory name.");
        }

        if (!Directory.Exists(sourceDirectoryPath))
        {
            throw new DirectoryNotFoundException($"Artifact snapshot source directory '{sourceDirectoryPath}' was not found.");
        }

        var layout = GetSessionLayout(appId, sessionId);
        var targetDirectoryPath = Path.Combine(layout.ArtifactsDirectoryPath, FileNameUtil.Sanitize(snapshot.ArtifactDirectoryName));
        lock (artifactGate)
        {
            Directory.CreateDirectory(layout.ArtifactsDirectoryPath);
            if (Directory.Exists(targetDirectoryPath))
            {
                Directory.Delete(targetDirectoryPath, recursive: true);
            }

            Directory.CreateDirectory(targetDirectoryPath);
            CopyDirectoryContents(sourceDirectoryPath, targetDirectoryPath);
        }
    }

    private static void CopyExtractedAppIcon(
        SessionPathLayout sourceLayout,
        SessionPathLayout targetLayout,
        SessionAppIcon? sourceAppIcon,
        SessionAppIcon? targetAppIcon)
    {
        if (sourceAppIcon is null || targetAppIcon is null)
        {
            return;
        }

        var sourceFilePath = SessionAppIconArtifactPath.ResolveSessionIconPath(sourceLayout.SessionDirectoryPath, sourceAppIcon);
        if (!File.Exists(sourceFilePath))
        {
            return;
        }

        Directory.CreateDirectory(targetLayout.SessionDirectoryPath);
        var targetFilePath = SessionAppIconArtifactPath.ResolveSessionIconPath(targetLayout.SessionDirectoryPath, targetAppIcon);
        Directory.CreateDirectory(Path.GetDirectoryName(targetFilePath)!);
        File.Copy(sourceFilePath, targetFilePath, overwrite: true);
    }

    private static void CopyExtractedCapturedImages(
        SessionPathLayout sourceLayout,
        SessionPathLayout targetLayout,
        IReadOnlyList<SessionImageFrame> frames)
    {
        foreach (var frame in frames)
        {
            var sourceFilePath = SessionImageArtifactPath.ResolveCapturedImagePath(sourceLayout.CapturedImagesDirectoryPath, frame);
            if (!File.Exists(sourceFilePath))
            {
                throw new InvalidDataException($"The source session is missing image data for frame '{frame.FrameId}'.");
            }

            var targetFilePath = SessionImageArtifactPath.ResolveCapturedImagePath(targetLayout.CapturedImagesDirectoryPath, frame);
            Directory.CreateDirectory(Path.GetDirectoryName(targetFilePath)!);
            File.Copy(sourceFilePath, targetFilePath, overwrite: true);
        }
    }

    private static void CopyExtractedArtifactPayloadDirectories(
        SessionPathLayout sourceLayout,
        SessionPathLayout targetLayout,
        IReadOnlyList<SessionArtifactSnapshot> artifactSnapshots)
    {
        foreach (var artifactSnapshot in artifactSnapshots)
        {
            if (string.IsNullOrWhiteSpace(artifactSnapshot.ArtifactDirectoryName))
            {
                continue;
            }

            var directoryName = FileNameUtil.Sanitize(artifactSnapshot.ArtifactDirectoryName);
            var sourceDirectoryPath = Path.Combine(sourceLayout.ArtifactsDirectoryPath, directoryName);
            if (!Directory.Exists(sourceDirectoryPath))
            {
                continue;
            }

            var targetDirectoryPath = Path.Combine(targetLayout.ArtifactsDirectoryPath, directoryName);
            Directory.CreateDirectory(targetDirectoryPath);
            CopyDirectoryContents(sourceDirectoryPath, targetDirectoryPath);
        }
    }

    public void QueueSave(AppSessionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var snapshotCopy = SessionSnapshotReader.CloneAsHistorical(snapshot);
        var sessionId = snapshotCopy.SessionId;
        var shouldEnqueue = false;
        lock (pendingSnapshotGate)
        {
            if (deletedSessionIds.Contains(sessionId))
            {
                return;
            }

            pendingSnapshotsBySessionId[sessionId] = snapshotCopy;
            if (queuedSnapshotSessionIds.Add(sessionId))
            {
                shouldEnqueue = true;
            }
        }

        if (shouldEnqueue)
        {
            pendingSnapshotQueue.Writer.TryWrite(sessionId);
        }
    }

    public void QueueLogBatch(SessionLogBatchEventArgs batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        if (batch.Entries.Count == 0 || IsDeleted(batch.SessionId))
        {
            return;
        }

        var key = new SessionLogPersistenceKey(batch.SessionId, batch.StreamId);
        var shouldEnqueue = false;
        lock (pendingLogBatchGate)
        {
            if (pendingLogBatches.TryGetValue(key, out var pendingBatch))
            {
                pendingBatch.Append(batch);
            }
            else
            {
                pendingLogBatches[key] = new SessionLogPersistenceBatch(batch);
            }

            shouldEnqueue = queuedLogBatchKeys.Add(key);
        }

        if (shouldEnqueue)
        {
            pendingLogBatchQueue.Writer.TryWrite(key);
        }
    }

    public Task<SessionImageFrame?> SaveSessionImageAsync(
        string appId,
        string sessionId,
        DateTimeOffset capturedAtUtc,
        string format,
        int width,
        int height,
        int quality,
        ReadOnlyMemory<byte> bytes,
        bool allowDuplicate = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(format);
        var taskCompletionSource = new TaskCompletionSource<SessionImageFrame?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pendingWrite = new PendingImageWrite(
            appId.Trim(),
            sessionId.Trim(),
            capturedAtUtc,
            format.Trim(),
            width,
            height,
            quality,
            bytes,
            allowDuplicate,
            taskCompletionSource);
        if (!pendingImageQueue.Writer.TryWrite(pendingWrite))
        {
            taskCompletionSource.TrySetResult(null);
        }

        return taskCompletionSource.Task;
    }

    public SessionAppIcon? SaveSessionAppIcon(string appId, string sessionId, DeviceApplicationIconProfile? icon)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        if (!TryDecodeApplicationIcon(icon, out var bytes))
        {
            return null;
        }

        var format = ResolveApplicationIconFormat(icon);
        var sessionIcon = new SessionAppIcon
        {
            FileName = SessionAppIconArtifactPath.BuildFileName(format),
            Format = format,
            MimeType = string.IsNullOrWhiteSpace(icon?.MimeType) ? ResolveApplicationIconMimeType(format) : icon.MimeType.Trim(),
            Width = icon?.Width is > 0 ? icon.Width : null,
            Height = icon?.Height is > 0 ? icon.Height : null,
            ByteCount = bytes.LongLength
        };

        var layout = GetSessionLayout(appId, sessionId);
        Directory.CreateDirectory(layout.SessionDirectoryPath);
        var filePath = SessionAppIconArtifactPath.ResolveSessionIconPath(layout.SessionDirectoryPath, sessionIcon);
        WriteAllBytesAtomic(filePath, bytes);
        return sessionIcon;
    }

    public bool Delete(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return false;
        }

        var trimmedSessionId = sessionId.Trim();
        string? filePath = null;
        string? archiveFilePath = null;

        lock (indexGate)
        {
            RefreshIndexIfStale();
            if (filePathBySessionId is not null
                && filePathBySessionId.TryGetValue(trimmedSessionId, out var indexedPath))
            {
                filePath = indexedPath;
                filePathBySessionId.Remove(trimmedSessionId);
                cacheSizeBytesBySessionId?.Remove(trimmedSessionId);
                summaryBySessionId?.Remove(trimmedSessionId);
                summarySnapshotBySessionId?.Remove(trimmedSessionId);
            }

            if (archivePathBySessionId is not null
                && archivePathBySessionId.Remove(trimmedSessionId, out var indexedArchivePath))
            {
                archiveFilePath = indexedArchivePath;
                cacheSizeBytesBySessionId?.Remove(trimmedSessionId);
                summaryBySessionId?.Remove(trimmedSessionId);
                summarySnapshotBySessionId?.Remove(trimmedSessionId);
            }
        }

        if (string.IsNullOrWhiteSpace(filePath) && string.IsNullOrWhiteSpace(archiveFilePath))
        {
            return false;
        }

        try
        {
            lock (pendingSnapshotGate)
            {
                pendingSnapshotsBySessionId.Remove(trimmedSessionId);
                queuedSnapshotSessionIds.Remove(trimmedSessionId);
                deletedSessionIds.Add(trimmedSessionId);
            }

            RunWithSessionPersistenceLock(trimmedSessionId, () =>
            {
                lock (artifactGate)
                {
                    lastArtifactBySessionId.Remove(trimmedSessionId);
                }

                incrementalPersistenceStateBySessionId.TryRemove(trimmedSessionId, out _);
                foreach (var key in persistedLogCountByStream.Keys
                             .Where(key => string.Equals(key.SessionId, trimmedSessionId, StringComparison.Ordinal))
                             .ToArray())
                {
                    persistedLogCountByStream.TryRemove(key, out _);
                }

                persistedStatusBySessionId.TryRemove(trimmedSessionId, out _);

                var sessionDirectoryPath = string.IsNullOrWhiteSpace(filePath)
                    ? null
                    : Path.GetDirectoryName(filePath);
                if (!string.IsNullOrWhiteSpace(archiveFilePath))
                {
                    DeleteIfExists(archiveFilePath);
                    sessionDirectoryPath ??= SessionCaptureArchiveCodec.GetSessionDirectoryPathFromArchivePath(archiveFilePath);
                }

                if (!string.IsNullOrWhiteSpace(sessionDirectoryPath) && Directory.Exists(sessionDirectoryPath))
                {
                    Directory.Delete(sessionDirectoryPath, recursive: true);
                }

                var appDirectoryPath = Path.GetDirectoryName(sessionDirectoryPath ?? string.Empty);
                if (!string.IsNullOrWhiteSpace(appDirectoryPath)
                    && Directory.Exists(appDirectoryPath)
                    && !Directory.EnumerateFileSystemEntries(appDirectoryPath).Any())
                {
                    Directory.Delete(appDirectoryPath);
                }

                TouchCapturesIndex();
            });

            return true;
        }
        catch
        {
            lock (pendingSnapshotGate)
            {
                deletedSessionIds.Remove(trimmedSessionId);
            }

            lock (indexGate)
            {
                ReloadIndex();
            }

            return false;
        }
    }

}
