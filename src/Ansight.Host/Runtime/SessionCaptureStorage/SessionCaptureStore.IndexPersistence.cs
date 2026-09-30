namespace Ansight.Host.Runtime.SessionCaptureStorage;

using static SessionCaptureFileSystem;

using AppLifecycleState = global::Ansight.AppLifecycleState;
using Ansight.Host;
using Ansight.Pairing.Models;
using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Threading.Channels;

internal sealed partial class SessionCaptureStore
{
    private bool TryGetSummaryFilePath(string sessionId, bool reloadIndex, out string filePath)
    {
        filePath = string.Empty;
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return false;
        }

        string? archiveFilePath = null;
        lock (indexGate)
        {
            if (reloadIndex)
            {
                RefreshIndexIfStale();
            }
            else
            {
                EnsureIndex();
            }

            if (filePathBySessionId is not null
                && filePathBySessionId.TryGetValue(sessionId.Trim(), out var resolvedFilePath))
            {
                filePath = resolvedFilePath;
                return true;
            }

            archivePathBySessionId?.TryGetValue(sessionId.Trim(), out archiveFilePath);
        }

        return !string.IsNullOrWhiteSpace(archiveFilePath)
               && TryExpandSessionArchive(sessionId.Trim(), archiveFilePath, out filePath);
    }

    private void ReloadIndex()
    {
        filePathBySessionId = new Dictionary<string, string>(StringComparer.Ordinal);
        archivePathBySessionId = new Dictionary<string, string>(StringComparer.Ordinal);
        cacheSizeBytesBySessionId = new Dictionary<string, long>(StringComparer.Ordinal);
        summaryBySessionId = new Dictionary<string, SessionCaptureSummary>(StringComparer.Ordinal);
        summarySnapshotBySessionId = new Dictionary<string, AppSessionSnapshot>(StringComparer.Ordinal);
        lastIndexRefreshUtc = DateTimeOffset.UtcNow;
        if (!Directory.Exists(capturesRootPath))
        {
            return;
        }

        foreach (var filePath in Directory.EnumerateFiles(capturesRootPath, SessionSummaryFileName, SearchOption.AllDirectories))
        {
            if (filePath.Contains(SessionCacheExpansionDirectoryMarker, StringComparison.Ordinal))
            {
                continue;
            }

            var summary = SessionSnapshotReader.TryLoadDocument<SessionCaptureSummaryDocument>(filePath)?.Session;
            if (summary is null || string.IsNullOrWhiteSpace(summary.SessionId))
            {
                continue;
            }

            var sessionId = summary.SessionId;
            filePathBySessionId[sessionId] = filePath;
            summaryBySessionId[sessionId] = summary;
            var summarySnapshot = SessionSnapshotReader.LoadSummary(filePath, summary);
            if (summarySnapshot is not null)
            {
                summarySnapshotBySessionId[sessionId] = summarySnapshot;
            }
            if (summary.CacheSizeBytes > 0 && !IsRecordingStatus(summary.Status))
            {
                cacheSizeBytesBySessionId[sessionId] = summary.CacheSizeBytes;
            }
        }

        foreach (var archiveFilePath in Directory.EnumerateDirectories(capturesRootPath)
                     .SelectMany(appDirectoryPath => Directory.EnumerateFiles(
                         appDirectoryPath,
                         $"*{SessionCacheArchiveSuffix}",
                         SearchOption.TopDirectoryOnly)))
        {
            var archiveSummary = SessionCaptureArchiveCodec.TryLoadSessionArchiveSummary(archiveFilePath);
            if (archiveSummary is null
                || string.IsNullOrWhiteSpace(archiveSummary.SessionId)
                || summaryBySessionId.ContainsKey(archiveSummary.SessionId))
            {
                continue;
            }

            var archiveSizeBytes = GetFileSize(archiveFilePath);
            var sessionId = archiveSummary.SessionId;
            archivePathBySessionId[sessionId] = archiveFilePath;
            summaryBySessionId[sessionId] = archiveSummary;
            summarySnapshotBySessionId[sessionId] = SessionCaptureArchiveCodec.CreateArchivedSummarySnapshot(
                archiveFilePath,
                archiveSummary,
                archiveSizeBytes);
            cacheSizeBytesBySessionId[sessionId] = archiveSizeBytes;
        }

        lastObservedCapturesWriteUtc = GetCapturesIndexWriteUtc();
    }

    private void EnsureIndex()
    {
        if (filePathBySessionId is not null
            && archivePathBySessionId is not null
            && cacheSizeBytesBySessionId is not null
            && summaryBySessionId is not null
            && summarySnapshotBySessionId is not null)
        {
            return;
        }

        ReloadIndex();
    }

    private void RefreshIndexIfStale()
    {
        var capturesWriteUtc = GetCapturesIndexWriteUtc();
        if (filePathBySessionId is null
            || archivePathBySessionId is null
            || cacheSizeBytesBySessionId is null
            || summaryBySessionId is null
            || summarySnapshotBySessionId is null
            || capturesWriteUtc != lastObservedCapturesWriteUtc
            || DateTimeOffset.UtcNow - lastIndexRefreshUtc >= IndexRefreshInterval)
        {
            ReloadIndex();
        }
    }

    private DateTime GetCapturesIndexWriteUtc()
    {
        try
        {
            return Directory.GetLastWriteTimeUtc(capturesRootPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return DateTime.MinValue;
        }
    }

    private DateTime TouchCapturesIndex()
    {
        try
        {
            Directory.SetLastWriteTimeUtc(capturesRootPath, DateTime.UtcNow);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return GetCapturesIndexWriteUtc();
    }

    private async Task ProcessPendingSnapshotsAsync()
    {
        await foreach (var sessionId in pendingSnapshotQueue.Reader.ReadAllAsync())
        {
            AppSessionSnapshot? snapshot;
            lock (pendingSnapshotGate)
            {
                pendingSnapshotsBySessionId.Remove(sessionId, out snapshot);
            }

            if (snapshot is not null)
            {
                try
                {
                    PersistSnapshot(snapshot);
                }
                catch
                {
                    // Keep the in-memory session live even if persistence fails.
                }
            }

            lock (pendingSnapshotGate)
            {
                if (pendingSnapshotsBySessionId.ContainsKey(sessionId))
                {
                    pendingSnapshotQueue.Writer.TryWrite(sessionId);
                }
                else
                {
                    queuedSnapshotSessionIds.Remove(sessionId);
                }
            }
        }
    }

    private async Task ProcessPendingLogBatchesAsync()
    {
        while (await pendingLogBatchQueue.Reader.WaitToReadAsync().ConfigureAwait(false))
        {
            var keys = new HashSet<SessionLogPersistenceKey>();
            while (pendingLogBatchQueue.Reader.TryRead(out var queuedKey))
            {
                keys.Add(queuedKey);
            }

            await Task.Delay(LogPersistenceBatchInterval).ConfigureAwait(false);
            while (pendingLogBatchQueue.Reader.TryRead(out var coalescedKey))
            {
                keys.Add(coalescedKey);
            }

            foreach (var key in keys)
            {
                SessionLogPersistenceBatch? batch;
                lock (pendingLogBatchGate)
                {
                    pendingLogBatches.Remove(key, out batch);
                }

                if (batch is not null)
                {
                    try
                    {
                        PersistLogBatch(batch);
                    }
                    catch
                    {
                        // Keep the in-memory session live even if persistence fails.
                    }
                }

                lock (pendingLogBatchGate)
                {
                    if (pendingLogBatches.ContainsKey(key))
                    {
                        pendingLogBatchQueue.Writer.TryWrite(key);
                    }
                    else
                    {
                        queuedLogBatchKeys.Remove(key);
                    }
                }
            }
        }
    }

    private void PersistLogBatch(SessionLogPersistenceBatch batch)
    {
        RunWithSessionPersistenceLock(batch.SessionId, () => PersistLogBatchCore(batch));
    }

    private void PersistLogBatchCore(SessionLogPersistenceBatch batch)
    {
        if (IsDeleted(batch.SessionId) || batch.Entries.Count == 0)
        {
            return;
        }

        var layout = GetSessionLayout(batch.AppId, batch.SessionId);
        var persistedStatus = persistedStatusBySessionId.GetOrAdd(
            batch.SessionId,
            _ => new PersistedSessionStatus(
                SessionSnapshotReader.TryLoadDocument<SessionCaptureSummaryDocument>(layout.SummaryFilePath)?.Session?.Status));

        if (!string.IsNullOrWhiteSpace(persistedStatus.Value) && !IsRecordingStatus(persistedStatus.Value))
        {
            return;
        }

        var key = new SessionLogPersistenceKey(batch.SessionId, batch.StreamId);
        if (!persistedLogCountByStream.TryGetValue(key, out var persistedStreamEntryCount))
        {
            persistedStreamEntryCount = SessionSnapshotReader.LoadLogs(layout.LogsFilePath, layout.LogSegmentsDirectoryPath)
                .Count(entry => string.Equals(entry.StreamId, batch.StreamId, StringComparison.Ordinal));
        }

        var firstUnpersistedIndex = Math.Max(0, persistedStreamEntryCount - batch.StartEntryIndex);
        if (firstUnpersistedIndex < batch.Entries.Count)
        {
            Directory.CreateDirectory(layout.SessionDirectoryPath);
            AppendJsonLine(
                GetLogsAppendFilePath(layout),
                new SessionLogsBlobDocument
                {
                    SavedAtUtc = DateTimeOffset.UtcNow,
                    Logs = CopyRange(
                        batch.Entries,
                        firstUnpersistedIndex,
                        batch.Entries.Count - firstUnpersistedIndex)
                });
        }

        persistedLogCountByStream[key] = Math.Max(persistedStreamEntryCount, batch.StreamEntryCount);
        if (incrementalPersistenceStateBySessionId.TryGetValue(batch.SessionId, out var persistenceState))
        {
            persistenceState.PersistedLogCount = Math.Max(
                persistenceState.PersistedLogCount,
                batch.TotalEntryCount);
        }
    }

    private void PersistSnapshot(AppSessionSnapshot snapshot)
    {
        RunWithSessionPersistenceLock(snapshot.SessionId, () =>
        {
            if (IsDeleted(snapshot.SessionId))
            {
                return;
            }

            foreach (var batch in TakePendingLogBatches(snapshot.SessionId))
            {
                PersistLogBatchCore(batch);
            }

            var previousStatus = ResolvePersistedStatus(snapshot);
            SaveSnapshot(snapshot);
            if (!snapshot.IsHistorical && snapshot.Status is "Connected" or "WebSocket Open"
                && previousStatus != snapshot.Status)
            {
                analytics.TrackAcquisition("cli_first_app_connected", once: true);
            }
            if (LocalSessionCaptureAnalytics.ShouldReportRecordedCapture(previousStatus, snapshot))
            {
                analytics.RecordUsage("capture",
                        outcome: LocalSessionCaptureAnalytics.CaptureUsageOutcome(snapshot),
                        durationSeconds: Math.Max(0, (snapshot.LastUpdatedUtc - snapshot.CreatedUtc).TotalSeconds),
                        hasEvidence: LocalSessionCaptureAnalytics.CreateCompletionProperties(snapshot)["hasEvidence"] is true);
                // Historical imports and empty connections are not activation.
                if (LocalSessionCaptureAnalytics.ShouldReportActivation(previousStatus, snapshot))
                    analytics.TrackAcquisition("cli_first_session_captured", once: true);
            }
            if (LocalSessionCaptureAnalytics.ShouldReportCompletion(previousStatus, snapshot))
                log.Event(
                    LocalSessionCaptureAnalytics.CompletedEventName,
                    LocalSessionCaptureAnalytics.CreateCompletionProperties(snapshot));
        });
    }

    private string? ResolvePersistedStatus(AppSessionSnapshot snapshot)
    {
        if (persistedStatusBySessionId.TryGetValue(snapshot.SessionId, out var persistedStatus))
        {
            return persistedStatus.Value;
        }

        var layout = GetSessionLayout(snapshot.AppId, snapshot.SessionId);
        return SessionSnapshotReader
            .TryLoadDocument<SessionCaptureSummaryDocument>(layout.SummaryFilePath)?
            .Session?
            .Status;
    }

    private IReadOnlyList<SessionLogPersistenceBatch> TakePendingLogBatches(string sessionId)
    {
        lock (pendingLogBatchGate)
        {
            var keys = pendingLogBatches.Keys
                .Where(key => string.Equals(key.SessionId, sessionId, StringComparison.Ordinal))
                .ToArray();
            if (keys.Length == 0)
            {
                return Array.Empty<SessionLogPersistenceBatch>();
            }

            var batches = new List<SessionLogPersistenceBatch>(keys.Length);
            foreach (var key in keys)
            {
                if (pendingLogBatches.Remove(key, out var batch))
                {
                    batches.Add(batch);
                }
            }

            return batches;
        }
    }

    private void AllowPersistence(string sessionId)
    {
        lock (pendingSnapshotGate)
        {
            deletedSessionIds.Remove(sessionId);
        }
    }

    private bool IsDeleted(string sessionId)
    {
        lock (pendingSnapshotGate)
        {
            return deletedSessionIds.Contains(sessionId);
        }
    }

    private async Task ProcessPendingImagesAsync()
    {
        await foreach (var request in pendingImageQueue.Reader.ReadAllAsync())
        {
            try
            {
                if (IsDeleted(request.SessionId))
                {
                    request.Completion.TrySetResult(null);
                    continue;
                }

                var frame = SaveSessionImage(
                    request.AppId,
                    request.SessionId,
                    request.CapturedAtUtc,
                    request.Format,
                    request.Width,
                    request.Height,
                    request.Quality,
                    request.Bytes,
                    request.AllowDuplicate);
                request.Completion.TrySetResult(frame);
            }
            catch (Exception ex)
            {
                request.Completion.TrySetException(ex);
            }
        }
    }
}
