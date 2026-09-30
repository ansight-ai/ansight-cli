namespace Ansight.Host.Runtime.State;

using AppLifecycleState = global::Ansight.AppLifecycleState;
using System.Text;
using System.Text.Json.Nodes;
using Ansight.Host;
using Ansight.Pairing.Models;
using Ansight.Infrastructure.Logging;
using Ansight.Infrastructure.Utilities;

internal sealed partial class RuntimeState
{
    private void UpdateSession(
        string sessionId,
        Action<SessionState> update,
        DateTimeOffset? timelineEndUtc = null,
        bool updateTimelineEnd = true,
        bool shouldPersist = true)
    {
        ArgumentNullException.ThrowIfNull(update);
        if (string.IsNullOrWhiteSpace(sessionId) || !EnsureSessionLoaded(sessionId))
        {
            return;
        }

        lock (gate)
        {
            if (!sessionsById.TryGetValue(sessionId, out var session))
            {
                return;
            }

            var wasActiveDeviceCapture = session.CaptureSource == WorkspaceExecutionModes.Device
                                         && !session.IsHistorical;
            update(session);
            if (updateTimelineEnd && (session.CaptureSource != WorkspaceExecutionModes.Device
                                      || wasActiveDeviceCapture))
            {
                SessionStateMapper.TouchTimelineEnd(session, timelineEndUtc ?? DateTimeOffset.UtcNow);
            }

            TouchRetainedLoadedSession(session);
            EnforceRetainedLoadedSessionLimit();
        }

        QueueSessionUpdated(sessionId, shouldPersist);
    }

    public void SetSessionDeviceProfile(string sessionId, DeviceAppProfile? profile, string? profileJson)
    {
        if (profile is null && string.IsNullOrWhiteSpace(profileJson))
        {
            return;
        }

        var appIcon = PersistSessionAppIcon(sessionId, profile?.App?.Icon);
        var sdkVersion = SessionSdkVersion.Resolve(profile, profileJson);
        UpdateSession(sessionId, session =>
        {
            session.DeviceProfile = profile;
            session.DeviceProfileJson = string.IsNullOrWhiteSpace(profileJson) ? session.DeviceProfileJson : profileJson;
            session.SdkVersion = sdkVersion ?? session.SdkVersion;
            session.AppIcon = appIcon ?? session.AppIcon;
        }, updateTimelineEnd: false);
    }

    public void SetSessionCustomProperties(string sessionId, JsonObject? customProperties)
    {
        UpdateSession(sessionId, session =>
        {
            session.CustomProperties = SessionSnapshotCloner.CloneCustomProperties(customProperties);
        }, updateTimelineEnd: false);
    }

    public void SetSessionAppToolCatalog(string sessionId, SessionAppToolCatalogSnapshot catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        UpdateSession(sessionId, session =>
        {
            session.AppToolCatalog = SessionSnapshotCloner.CloneAppToolCatalog(catalog);
        }, updateTimelineEnd: false);
    }

    public void AddSessionImage(string sessionId, DateTimeOffset capturedAtUtc, string format, int width, int height, int quality, ReadOnlyMemory<byte> bytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(format);

        if (bytes.Length == 0 || !EnsureSessionLoaded(sessionId))
        {
            return;
        }

        string appId;
        lock (gate)
        {
            if (!sessionsById.TryGetValue(sessionId, out var session))
            {
                return;
            }

            appId = session.AppId;
        }

        PersistSessionImageAsync(sessionId, appId, capturedAtUtc, format, width, height, quality, bytes)
            .SafeFireAndForget();
    }

    public async Task<SessionImageFrame?> AddSessionEvidenceImageAsync(
        string sessionId,
        DateTimeOffset capturedAtUtc,
        string format,
        int width,
        int height,
        int quality,
        ReadOnlyMemory<byte> bytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(format);
        if (bytes.Length == 0 || !EnsureSessionLoaded(sessionId))
        {
            return null;
        }

        string appId;
        lock (gate)
        {
            if (!sessionsById.TryGetValue(sessionId, out var session))
            {
                return null;
            }

            appId = session.AppId;
        }

        var frame = await sessionCaptureStore.SaveSessionImageAsync(
            appId,
            sessionId,
            capturedAtUtc,
            format,
            width,
            height,
            quality,
            bytes,
            allowDuplicate: true);
        if (frame is null)
        {
            return null;
        }

        UpdateSession(sessionId, session =>
        {
            session.Images.Add(frame);
            session.MarkImagesChanged();
        }, frame.CapturedAtUtc);
        return frame;
    }

    public void AddSessionAnalysis(string sessionId, SessionAnalysisRecord analysis)
    {
        ArgumentNullException.ThrowIfNull(analysis);

        UpdateSession(sessionId, session =>
        {
            session.Analyses.Add(analysis);
            session.MarkAnalysesChanged();
        }, updateTimelineEnd: false);
    }

    private bool EnsureSessionLoaded(string sessionId)
    {
        lock (gate)
        {
            if (sessionsById.TryGetValue(sessionId, out var loadedSession))
            {
                TouchRetainedLoadedSession(loadedSession);
                EnforceRetainedLoadedSessionLimit();
                return true;
            }
        }

        if (!sessionCaptureStore.TryLoad(sessionId, out var snapshot) || snapshot is null)
        {
            return false;
        }

        lock (gate)
        {
            if (!sessionsById.ContainsKey(sessionId))
            {
                sessionsById[sessionId] = SessionStateMapper.CreateState(snapshot);
            }

            TouchRetainedLoadedSession(sessionsById[sessionId]);
            EnforceRetainedLoadedSessionLimit();
        }

        return true;
    }

    private void TouchRetainedLoadedSession(SessionState session)
    {
        if (!IsRetainedLoadedSessionCandidate(session))
        {
            RemoveRetainedLoadedSession(session.SessionId);
            return;
        }

        if (retainedLoadedSessionNodeById.TryGetValue(session.SessionId, out var existingNode))
        {
            retainedLoadedSessionIds.Remove(existingNode);
        }

        retainedLoadedSessionNodeById[session.SessionId] = retainedLoadedSessionIds.AddLast(session.SessionId);
    }

    private void EnforceRetainedLoadedSessionLimit()
    {
        while (retainedLoadedSessionNodeById.Count > MaximumRetainedLoadedSessionCount)
        {
            var oldestNode = retainedLoadedSessionIds.First;
            if (oldestNode is null)
            {
                retainedLoadedSessionNodeById.Clear();
                return;
            }

            var sessionId = oldestNode.Value;
            RemoveRetainedLoadedSession(sessionId);
            if (sessionsById.TryGetValue(sessionId, out var session)
                && IsRetainedLoadedSessionCandidate(session))
            {
                sessionCaptureStore.QueueSave(SessionStateMapper.CreateSnapshot(session));
                sessionsById.Remove(sessionId);
            }
        }
    }

    private void RemoveRetainedLoadedSession(string sessionId)
    {
        if (!retainedLoadedSessionNodeById.Remove(sessionId, out var node))
        {
            return;
        }

        retainedLoadedSessionIds.Remove(node);
    }

    private static bool IsRetainedLoadedSessionCandidate(SessionState session)
    {
        return session.IsHistorical && !IsSessionLive(session);
    }

    private void EnsureNextSessionNumberInitialized()
    {
        lock (gate)
        {
            if (nextSessionNumberInitialized)
            {
                return;
            }
        }

        var highestPersistedNumber = sessionCaptureStore.GetHighestSessionNumber();

        lock (gate)
        {
            if (nextSessionNumberInitialized)
            {
                return;
            }

            var highestLoadedNumber = sessionsById.Keys
                .Select(ParseSessionNumber)
                .DefaultIfEmpty(0)
                .Max();

            nextSessionNumber = Math.Max(nextSessionNumber, Math.Max(highestPersistedNumber, highestLoadedNumber) + 1);
            nextSessionNumberInitialized = true;
        }
    }

    private void PersistAndBroadcast(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        QueueSessionUpdated(sessionId, shouldPersist: true);
    }

    private void QueueSessionUpdated(string sessionId, bool shouldPersist = false)
    {
        lock (broadcastGate)
        {
            pendingSessionUpdateIds.Add(sessionId);
            if (shouldPersist)
            {
                pendingSessionPersistenceIds.Add(sessionId);
            }

            if (sessionUpdateBroadcastTimer is not null)
            {
                return;
            }

            sessionUpdateBroadcastTimer = new Timer(
                FlushPendingSessionUpdates,
                null,
                SessionUpdateBroadcastInterval,
                SessionUpdateBroadcastInterval);
        }
    }

    private void FlushPendingSessionUpdates(object? state)
    {
        if (Interlocked.Exchange(ref sessionUpdateFlushActive, 1) != 0)
        {
            return;
        }

        try
        {
            FlushPendingSessionUpdatesCore();
        }
        finally
        {
            Volatile.Write(ref sessionUpdateFlushActive, 0);
        }
    }

    private void FlushPendingSessionUpdatesCore()
    {
        string[] sessionIdsToBroadcast;
        string[] pendingPersistenceSessionIds;
        HashSet<string> existingSessionIds;
        HashSet<string> terminalSessionIds;
        HashSet<string> sessionIdsToPersist = new(StringComparer.Ordinal);
        AppSessionSnapshot[] snapshotsToBroadcast;
        AppSessionSnapshot[] snapshotsToPersist;
        Timer? timerToDispose = null;
        var nowUtc = DateTimeOffset.UtcNow;

        lock (broadcastGate)
        {
            sessionIdsToBroadcast = pendingSessionUpdateIds.ToArray();
            pendingPersistenceSessionIds = pendingSessionPersistenceIds.ToArray();
            pendingSessionUpdateIds.Clear();
        }

        lock (gate)
        {
            existingSessionIds = pendingPersistenceSessionIds
                .Where(sessionsById.ContainsKey)
                .ToHashSet(StringComparer.Ordinal);
            terminalSessionIds = existingSessionIds
                .Where(sessionId => IsTerminalSessionStatus(sessionsById[sessionId].Status))
                .ToHashSet(StringComparer.Ordinal);
        }

        lock (broadcastGate)
        {
            foreach (var sessionId in pendingPersistenceSessionIds)
            {
                if (!existingSessionIds.Contains(sessionId))
                {
                    pendingSessionPersistenceIds.Remove(sessionId);
                    lastSessionPersistenceUtcById.Remove(sessionId);
                    continue;
                }

                if (!terminalSessionIds.Contains(sessionId)
                    && lastSessionPersistenceUtcById.TryGetValue(sessionId, out var lastPersistenceUtc)
                    && nowUtc - lastPersistenceUtc < SessionPersistenceInterval)
                {
                    continue;
                }

                if (pendingSessionPersistenceIds.Remove(sessionId))
                {
                    sessionIdsToPersist.Add(sessionId);
                    if (terminalSessionIds.Contains(sessionId))
                    {
                        lastSessionPersistenceUtcById.Remove(sessionId);
                    }
                    else
                    {
                        lastSessionPersistenceUtcById[sessionId] = nowUtc;
                    }
                }
            }

            if (pendingSessionUpdateIds.Count == 0 && pendingSessionPersistenceIds.Count == 0)
            {
                timerToDispose = sessionUpdateBroadcastTimer;
                sessionUpdateBroadcastTimer = null;
            }
        }

        timerToDispose?.Dispose();

        lock (gate)
        {
            var broadcasts = new List<AppSessionSnapshot>(sessionIdsToBroadcast.Length);
            var persists = new List<AppSessionSnapshot>(sessionIdsToPersist.Count);
            foreach (var sessionId in sessionIdsToBroadcast)
            {
                if (!sessionsById.TryGetValue(sessionId, out var session))
                {
                    continue;
                }

                if (sessionIdsToPersist.Contains(sessionId))
                {
                    persists.Add(SessionStateMapper.CreateSnapshot(session));
                }

                broadcasts.Add(SessionStateMapper.CreateSnapshot(session, SessionSnapshotContent.Summary));
            }

            foreach (var sessionId in sessionIdsToPersist.Except(sessionIdsToBroadcast, StringComparer.Ordinal))
            {
                if (sessionsById.TryGetValue(sessionId, out var session))
                {
                    persists.Add(SessionStateMapper.CreateSnapshot(session));
                }
            }

            snapshotsToBroadcast = broadcasts
                .OrderByDescending(snapshot => snapshot.LastUpdatedUtc)
                .ToArray();
            snapshotsToPersist = persists.ToArray();
        }

        foreach (var snapshot in snapshotsToPersist)
        {
            sessionCaptureStore.QueueSave(snapshot);
        }

        foreach (var snapshot in snapshotsToBroadcast)
        {
            SessionUpdated?.Invoke(this, snapshot);
        }
    }
}
