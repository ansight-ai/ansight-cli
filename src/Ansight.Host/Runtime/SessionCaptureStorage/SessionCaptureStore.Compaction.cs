namespace Ansight.Host.Runtime.SessionCaptureStorage;

using System.Globalization;
using System.IO.Compression;
using Ansight.Pairing.Models;
using static SessionCaptureFileSystem;

internal sealed partial class SessionCaptureStore
{
    public int CompactSessionsOlderThan(
        int compactionAgeDays,
        DateTimeOffset nowUtc,
        ISet<string>? protectedSessionIds = null)
    {
        var cutoffUtc = nowUtc.ToUniversalTime().AddDays(-Math.Max(1, compactionAgeDays));
        string[] sessionIds;
        lock (indexGate)
        {
            RefreshIndexIfStale();
            sessionIds = summaryBySessionId?.Values
                .Where(summary => !IsRecordingStatus(summary.Status)
                                  && protectedSessionIds?.Contains(summary.SessionId) != true
                                  && filePathBySessionId?.TryGetValue(summary.SessionId, out var summaryFilePath) == true
                                  && SessionCaptureArchiveCodec.IsSessionPastCompactionCutoff(summary, summaryFilePath, cutoffUtc))
                .OrderBy(summary => summary.LastUpdatedUtc)
                .Select(summary => summary.SessionId)
                .ToArray() ?? [];
        }

        var compactedCount = 0;
        foreach (var sessionId in sessionIds)
        {
            if (TryCompactSession(sessionId, cutoffUtc, protectedSessionIds))
            {
                compactedCount++;
            }
        }

        return compactedCount;
    }

    private bool TryCompactSession(
        string sessionId,
        DateTimeOffset cutoffUtc,
        ISet<string>? protectedSessionIds)
    {
        try
        {
            return RunWithSessionPersistenceLock(sessionId, () =>
            {
                string? summaryFilePath;
                SessionCaptureSummary? summary;
                lock (indexGate)
                {
                    EnsureIndex();
                    filePathBySessionId!.TryGetValue(sessionId, out summaryFilePath);
                    summaryBySessionId!.TryGetValue(sessionId, out summary);
                }

                if (string.IsNullOrWhiteSpace(summaryFilePath)
                    || summary is null
                    || !SessionCaptureArchiveCodec.IsSessionPastCompactionCutoff(summary, summaryFilePath, cutoffUtc)
                    || IsRecordingStatus(summary.Status)
                    || protectedSessionIds?.Contains(sessionId) == true)
                {
                    return false;
                }

                var sessionDirectoryPath = Path.GetDirectoryName(summaryFilePath);
                if (string.IsNullOrWhiteSpace(sessionDirectoryPath)
                    || !Directory.Exists(sessionDirectoryPath))
                {
                    return false;
                }

                var archiveFilePath = SessionCaptureArchiveCodec.GetSessionArchivePath(sessionDirectoryPath);
                var temporaryArchiveFilePath = $"{archiveFilePath}.{Guid.NewGuid():N}.tmp";
                var archivePublished = false;
                try
                {
                    ZipFile.CreateFromDirectory(
                        sessionDirectoryPath,
                        temporaryArchiveFilePath,
                        CompressionLevel.Optimal,
                        includeBaseDirectory: false);

                    var archivedSummary = SessionCaptureArchiveCodec.TryLoadSessionArchiveSummary(temporaryArchiveFilePath);
                    if (archivedSummary is null
                        || !string.Equals(archivedSummary.SessionId, sessionId, StringComparison.Ordinal))
                    {
                        throw new InvalidDataException("The compacted session archive does not contain the expected session summary.");
                    }

                    File.Move(temporaryArchiveFilePath, archiveFilePath, overwrite: true);
                    archivePublished = true;
                    Directory.Delete(sessionDirectoryPath, recursive: true);

                    var archiveSizeBytes = GetFileSize(archiveFilePath);
                    var summarySnapshot = SessionCaptureArchiveCodec.CreateArchivedSummarySnapshot(
                        archiveFilePath,
                        archivedSummary,
                        archiveSizeBytes);
                    var capturesWriteUtc = TouchCapturesIndex();
                    lock (indexGate)
                    {
                        EnsureIndex();
                        filePathBySessionId!.Remove(sessionId);
                        archivePathBySessionId![sessionId] = archiveFilePath;
                        summaryBySessionId![sessionId] = archivedSummary;
                        summarySnapshotBySessionId![sessionId] = summarySnapshot;
                        cacheSizeBytesBySessionId![sessionId] = archiveSizeBytes;
                        lastObservedCapturesWriteUtc = capturesWriteUtc;
                    }

                    return true;
                }
                catch
                {
                    if (archivePublished && Directory.Exists(sessionDirectoryPath))
                    {
                        TryDeleteFile(archiveFilePath);
                    }

                    throw;
                }
                finally
                {
                    TryDeleteFile(temporaryArchiveFilePath);
                }
            });
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private bool TryExpandSessionArchive(
        string sessionId,
        string archiveFilePath,
        out string summaryFilePath)
    {
        var resolvedSummaryFilePath = string.Empty;
        var expanded = false;
        try
        {
            expanded = RunWithSessionPersistenceLock(sessionId, () =>
            {
                var sessionDirectoryPath = SessionCaptureArchiveCodec.GetSessionDirectoryPathFromArchivePath(archiveFilePath);
                var targetSummaryFilePath = Path.Combine(sessionDirectoryPath, SessionSummaryFileName);
                if (File.Exists(targetSummaryFilePath))
                {
                    SessionCaptureArchiveCodec.ResetSessionCompactionAge(sessionDirectoryPath, DateTimeOffset.UtcNow);
                    TryDeleteFile(archiveFilePath);
                    resolvedSummaryFilePath = targetSummaryFilePath;
                    UpdateExpandedSessionIndex(sessionId, targetSummaryFilePath);
                    return true;
                }

                if (!File.Exists(archiveFilePath))
                {
                    return false;
                }

                var temporaryDirectoryPath = $"{sessionDirectoryPath}{SessionCacheExpansionDirectoryMarker}{Guid.NewGuid():N}.tmp";
                try
                {
                    ZipFile.ExtractToDirectory(archiveFilePath, temporaryDirectoryPath);
                    var temporarySummaryFilePath = Path.Combine(temporaryDirectoryPath, SessionSummaryFileName);
                    var summary = SessionSnapshotReader.TryLoadDocument<SessionCaptureSummaryDocument>(temporarySummaryFilePath)?.Session;
                    if (summary is null
                        || !string.Equals(summary.SessionId, sessionId, StringComparison.Ordinal))
                    {
                        throw new InvalidDataException("The compacted session archive does not contain the expected session summary.");
                    }

                    Directory.Move(temporaryDirectoryPath, sessionDirectoryPath);
                    SessionCaptureArchiveCodec.ResetSessionCompactionAge(sessionDirectoryPath, DateTimeOffset.UtcNow);
                    TryDeleteFile(archiveFilePath);
                    resolvedSummaryFilePath = targetSummaryFilePath;
                    UpdateExpandedSessionIndex(sessionId, targetSummaryFilePath);
                    return true;
                }
                finally
                {
                    TryDeleteDirectory(temporaryDirectoryPath);
                }
            });
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            expanded = false;
        }

        summaryFilePath = resolvedSummaryFilePath;
        return expanded;
    }

    private void UpdateExpandedSessionIndex(
        string sessionId,
        string summaryFilePath)
    {
        var summary = SessionSnapshotReader.TryLoadDocument<SessionCaptureSummaryDocument>(summaryFilePath)?.Session;
        if (summary is null)
        {
            return;
        }

        var summarySnapshot = SessionSnapshotReader.LoadSummary(summaryFilePath, summary);
        var sessionDirectoryPath = Path.GetDirectoryName(summaryFilePath) ?? string.Empty;
        var cacheSizeBytes = GetDirectorySize(sessionDirectoryPath);
        var capturesWriteUtc = TouchCapturesIndex();
        lock (indexGate)
        {
            EnsureIndex();
            archivePathBySessionId!.Remove(sessionId);
            filePathBySessionId![sessionId] = summaryFilePath;
            summaryBySessionId![sessionId] = summary;
            if (summarySnapshot is not null)
            {
                summarySnapshotBySessionId![sessionId] = summarySnapshot;
            }

            cacheSizeBytesBySessionId![sessionId] = cacheSizeBytes;
            lastObservedCapturesWriteUtc = capturesWriteUtc;
        }
    }

}
