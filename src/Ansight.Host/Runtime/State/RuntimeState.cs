namespace Ansight.Host.Runtime.State;

using AppLifecycleState = global::Ansight.AppLifecycleState;
using System.Text;
using System.Text.Json.Nodes;
using Ansight.Host;
using Ansight.Pairing.Models;
using Ansight.Infrastructure.Logging;
using static RuntimeSnapshotNormalizer;

[Export(typeof(IRuntimeState))]
internal sealed partial class RuntimeState : IRuntimeState
{
    private const int MaximumRetainedLoadedSessionCount = 10;
    private const int MaximumRetainedSessionLogCount = 100_000;
    private const int MaximumRetainedLogStreamEntryCount = 50_000;
    private const int SessionLogTrimCount = 10_000;
    private const int LogStreamTrimCount = 5_000;
    private const int MaximumSessionAppHintLength = 48;
    private const string DefaultSessionAppHint = "app";
    private const string AppStateApplicationEventIdPrefix = "ansight-app-state:";
    internal const string DefaultSessionAnnotationSource = "ansight";
    private static readonly TimeSpan SessionUpdateBroadcastInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan SessionPersistenceInterval = TimeSpan.FromMilliseconds(750);
    private static readonly ILogger log = Logger.Create();
    private readonly object gate = new();
    private readonly object broadcastGate = new();
    private readonly Dictionary<string, SessionState> sessionsById = new(StringComparer.Ordinal);
    private readonly LinkedList<string> retainedLoadedSessionIds = [];
    private readonly Dictionary<string, LinkedListNode<string>> retainedLoadedSessionNodeById = new(StringComparer.Ordinal);
    private readonly HashSet<string> pendingSessionUpdateIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> pendingSessionPersistenceIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> lastSessionPersistenceUtcById = new(StringComparer.Ordinal);
    private readonly SessionCaptureStore sessionCaptureStore;
    private readonly SessionNormalizationService sessionNormalizationService;
    private Timer? sessionUpdateBroadcastTimer;
    private int sessionUpdateFlushActive;
    private bool nextSessionNumberInitialized;
    private int nextSessionNumber = 1;

    [ImportingConstructor]
    public RuntimeState(
        SessionCaptureStore sessionCaptureStore)
    {
        this.sessionCaptureStore = sessionCaptureStore;
        sessionNormalizationService = new SessionNormalizationService(sessionCaptureStore.CapturesRootPath);
    }

    public event EventHandler<LogEntry>? LogAdded;
    public event EventHandler<RuntimeEvent>? RuntimeEventOccurred;
    public event EventHandler<AppSessionSnapshot>? SessionUpdated;
    public event EventHandler<SessionLogBatchEventArgs>? SessionLogsAdded;
    public event EventHandler<string>? SessionDeleted;
    public event EventHandler<string>? ServerStatusChanged;

    public bool IsServerRunning { get; private set; }

    public string ServerStatusText { get; private set; } = "UDP pairing listener not started.";

    public IReadOnlyList<AppSessionSnapshot> GetSessionSummaries()
    {
        var persistedSnapshots = sessionCaptureStore.LoadSummaries();

        lock (gate)
        {
            var loadedSessionIds = sessionsById.Keys.ToHashSet(StringComparer.Ordinal);

            return sessionsById.Values
                .OrderByDescending(state => state.LastUpdatedUtc)
                .Select(state => SessionStateMapper.CreateSnapshot(state, SessionSnapshotContent.Summary))
                .Concat(persistedSnapshots.Where(snapshot => !loadedSessionIds.Contains(snapshot.SessionId)))
                .OrderByDescending(snapshot => snapshot.LastUpdatedUtc)
                .ToArray();
        }
    }

    public bool TryGetSessionContext(string sessionId, out RuntimeSessionContext? context)
    {
        context = null;
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return false;
        }

        lock (gate)
        {
            if (!sessionsById.TryGetValue(sessionId.Trim(), out var session))
            {
                return false;
            }

            context = new RuntimeSessionContext(session.AppId, session.ClientName, session.Status, session.CaptureSource);
            return true;
        }
    }

    public bool TryGetSessionSnapshot(string sessionId, out AppSessionSnapshot? snapshot)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            snapshot = null;
            return false;
        }

        AppSessionSnapshot? loadedStateSnapshot = null;
        lock (gate)
        {
            if (sessionsById.TryGetValue(sessionId, out var session))
            {
                TouchRetainedLoadedSession(session);
                EnforceRetainedLoadedSessionLimit();
                loadedStateSnapshot = SessionStateMapper.CreateSnapshot(session);
            }
        }

        if (loadedStateSnapshot is not null)
        {
            snapshot = IsSessionStatusLive(loadedStateSnapshot.Status)
                ? loadedStateSnapshot
                : sessionCaptureStore.ExpandRetainedLogs(loadedStateSnapshot);
            return true;
        }

        if (!sessionCaptureStore.TryLoad(sessionId, out var persistedSnapshot) || persistedSnapshot is null)
        {
            snapshot = null;
            return false;
        }

        lock (gate)
        {
            if (!sessionsById.TryGetValue(sessionId, out var session))
            {
                session = SessionStateMapper.CreateState(persistedSnapshot);
                sessionsById[sessionId] = session;
            }

            TouchRetainedLoadedSession(session);
            EnforceRetainedLoadedSessionLimit();
            snapshot = SessionStateMapper.CreateSnapshot(session);
            return true;
        }
    }

    public bool TryGetSessionLiveContentSnapshot(string sessionId, out AppSessionSnapshot? snapshot)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            snapshot = null;
            return false;
        }

        lock (gate)
        {
            if (!sessionsById.TryGetValue(sessionId.Trim(), out var session))
            {
                snapshot = null;
                return false;
            }

            TouchRetainedLoadedSession(session);
            EnforceRetainedLoadedSessionLimit();
            snapshot = SessionStateMapper.CreateSnapshot(session, SessionSnapshotContent.LiveContent);
            return true;
        }
    }

    public bool TryGetSessionReplaySnapshot(string sessionId, out AppSessionSnapshot? snapshot)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            snapshot = null;
            return false;
        }

        lock (gate)
        {
            if (!sessionsById.TryGetValue(sessionId.Trim(), out var session))
            {
                snapshot = null;
                return false;
            }

            TouchRetainedLoadedSession(session);
            EnforceRetainedLoadedSessionLimit();
            snapshot = SessionStateMapper.CreateSnapshot(session, SessionSnapshotContent.Replay);
            return true;
        }
    }

    public bool TryGetSessionSnapshotForExport(string sessionId, out AppSessionSnapshot? snapshot)
    {
        if (!TryGetSessionSnapshot(sessionId, out snapshot) || snapshot is null)
        {
            return false;
        }

        snapshot = sessionCaptureStore.ExpandRetainedLogs(snapshot);
        return true;
    }

    public async Task<AppSessionSnapshot?> LoadSessionSnapshotAsync(
        string sessionId,
        IProgress<SessionLoadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return null;
        }

        var trimmedSessionId = sessionId.Trim();
        AppSessionSnapshot? loadedStateSnapshot = null;
        lock (gate)
        {
            if (sessionsById.TryGetValue(trimmedSessionId, out var session))
            {
                TouchRetainedLoadedSession(session);
                EnforceRetainedLoadedSessionLimit();
                loadedStateSnapshot = SessionStateMapper.CreateSnapshot(session);
            }
        }

        if (loadedStateSnapshot is not null)
        {
            progress?.Report(SessionLoadProgress.Complete("Session content is already in memory."));
            return IsSessionStatusLive(loadedStateSnapshot.Status)
                ? loadedStateSnapshot
                : sessionCaptureStore.ExpandRetainedLogs(loadedStateSnapshot);
        }

        var loadedSnapshot = await sessionCaptureStore.LoadAsync(trimmedSessionId, progress, cancellationToken).ConfigureAwait(false);
        if (loadedSnapshot is null)
        {
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            if (!sessionsById.TryGetValue(trimmedSessionId, out var session))
            {
                session = SessionStateMapper.CreateState(loadedSnapshot);
                sessionsById[trimmedSessionId] = session;
            }

            TouchRetainedLoadedSession(session);
            EnforceRetainedLoadedSessionLimit();
            progress?.Report(SessionLoadProgress.Complete("Session content loaded."));
            return SessionStateMapper.CreateSnapshot(session);
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
        if (sessionCaptureStore.TryGetSessionCacheSizeBytes(trimmedSessionId, out cacheSizeBytes))
        {
            lock (gate)
            {
                if (sessionsById.TryGetValue(trimmedSessionId, out var session))
                {
                    session.CacheSizeBytes = cacheSizeBytes;
                }
            }

            return true;
        }

        lock (gate)
        {
            if (!sessionsById.TryGetValue(trimmedSessionId, out var loadedSession))
            {
                return false;
            }

            cacheSizeBytes = loadedSession.CacheSizeBytes;
            return cacheSizeBytes >= 0;
        }
    }

    public bool ExpandSessionCache(string sessionId)
        => sessionCaptureStore.ExpandSessionCache(sessionId);

    public int CompactSessionCache(
        int compactionAgeDays,
        DateTimeOffset nowUtc,
        ISet<string>? protectedSessionIds = null)
    {
        var effectiveProtectedSessionIds = protectedSessionIds?.ToHashSet(StringComparer.Ordinal)
                                           ?? new HashSet<string>(StringComparer.Ordinal);
        lock (gate)
        {
            foreach (var session in sessionsById.Values.Where(session => IsSessionStatusLive(session.Status)))
            {
                effectiveProtectedSessionIds.Add(session.SessionId);
            }
        }

        return sessionCaptureStore.CompactSessionsOlderThan(
            compactionAgeDays,
            nowUtc,
            effectiveProtectedSessionIds);
    }

    public void SetServerStatus(bool isRunning, string message)
    {
        IsServerRunning = isRunning;
        ServerStatusText = message;
        ServerStatusChanged?.Invoke(this, message);
        LogHost(message);
    }

    public void LogHost(string message)
    {
        var entry = new LogEntry(DateTimeOffset.UtcNow, message)
        {
            Source = "Host"
        };
        LogAdded?.Invoke(this, entry);
    }

    public void PublishRuntimeEvent(RuntimeEvent runtimeEvent)
    {
        ArgumentNullException.ThrowIfNull(runtimeEvent);
        RuntimeEventOccurred?.Invoke(this, runtimeEvent);
    }

    public string ReserveSessionId(string appId, string? processSessionId)
    {
        EnsureNextSessionNumberInitialized();

        var safeAppId = string.IsNullOrWhiteSpace(appId) ? "unknown" : appId.Trim();
        var safeProcessSessionId = NormalizeProcessSessionId(processSessionId);
        if (!string.IsNullOrWhiteSpace(safeProcessSessionId)
            && TryResolveExistingSessionId(safeAppId, safeProcessSessionId, out var existingSessionId))
        {
            return existingSessionId;
        }

        return AllocateAvailableSessionId(safeAppId);
    }

    public string CreateSession(
        string appId,
        string clientName,
        IPAddress remoteAddress,
        string? configId,
        string? processSessionId,
        string? reservedSessionId = null)
    {
        EnsureNextSessionNumberInitialized();

        var safeAppId = string.IsNullOrWhiteSpace(appId) ? "unknown" : appId.Trim();
        var safeClientName = string.IsNullOrWhiteSpace(clientName) ? "unknown" : clientName.Trim();
        var safeConfigId = string.IsNullOrWhiteSpace(configId) ? null : configId.Trim();
        var safeProcessSessionId = NormalizeProcessSessionId(processSessionId);
        var safeRemoteAddress = remoteAddress.ToString();
        string sessionId;
        var resumedExistingSession = false;

        if (!string.IsNullOrWhiteSpace(safeProcessSessionId)
            && TryResolveExistingSessionId(safeAppId, safeProcessSessionId, out var existingSessionId))
        {
            EnsureSessionLoaded(existingSessionId);

            lock (gate)
            {
                if (sessionsById.TryGetValue(existingSessionId, out var existingSession)
                    && string.Equals(existingSession.AppId, safeAppId, StringComparison.Ordinal)
                    && string.Equals(existingSession.ProcessSessionId, safeProcessSessionId, StringComparison.Ordinal))
                {
                    existingSession.ClientName = safeClientName;
                    existingSession.RemoteAddress = safeRemoteAddress;
                    existingSession.ConfigId = safeConfigId;
                    existingSession.ProcessSessionId = safeProcessSessionId;
                    existingSession.IsHistorical = false;
                    existingSession.Status = "Connected";
                    existingSession.LastUpdatedUtc = DateTimeOffset.UtcNow;

                    sessionId = existingSession.SessionId;
                    resumedExistingSession = true;
                }
                else
                {
                    sessionId = string.Empty;
                }
            }

            if (resumedExistingSession)
            {
                PersistAndBroadcast(sessionId);
                log.Info($"session_resumed sessionId={sessionId} appId={safeAppId} clientName={safeClientName} remoteAddress={safeRemoteAddress} configId={safeConfigId} processSessionId={safeProcessSessionId}");
                return sessionId;
            }
        }

        sessionId = string.IsNullOrWhiteSpace(reservedSessionId)
            ? AllocateAvailableSessionId(safeAppId)
            : reservedSessionId.Trim();

        lock (gate)
        {
            var state = new SessionState
            {
                SessionId = sessionId,
                AppId = safeAppId,
                ClientName = safeClientName,
                RemoteAddress = safeRemoteAddress,
                CreatedUtc = DateTimeOffset.UtcNow,
                LastUpdatedUtc = DateTimeOffset.UtcNow,
                ConfigId = safeConfigId,
                ProcessSessionId = safeProcessSessionId,
                Status = "Connected",
                Author = ResolveCurrentAuthor()
            };

            sessionsById[sessionId] = state;
        }

        PersistAndBroadcast(sessionId);
        log.Info($"session_created sessionId={sessionId} appId={safeAppId} clientName={safeClientName} remoteAddress={safeRemoteAddress} configId={safeConfigId} processSessionId={safeProcessSessionId}");
        return sessionId;
    }

    public int BeginSessionConnection(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || !EnsureSessionLoaded(sessionId))
        {
            return 1;
        }

        lock (gate)
        {
            if (!sessionsById.TryGetValue(sessionId, out var session))
            {
                return 1;
            }

            var nextSegmentId = Math.Max(1, session.NextTelemetrySegmentId);
            session.NextTelemetrySegmentId = nextSegmentId + 1;
            return nextSegmentId;
        }
    }

    public void SetSessionStatus(string sessionId, string status, string? message = null)
    {
        UpdateSession(sessionId, session =>
        {
            session.Status = status;
            if (IsTerminalSessionStatus(status))
            {
                session.IsHistorical = true;
            }
        });

        log.Info($"session_status_changed sessionId={sessionId} status={status} message={message}");
    }

    public void SetSessionAppState(string sessionId, AppLifecycleState state, DateTimeOffset? changedAtUtc = null)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || !EnsureSessionLoaded(sessionId))
        {
            return;
        }

        RuntimeClientAppStateChangedEvent? runtimeEvent = null;

        lock (gate)
        {
            if (!sessionsById.TryGetValue(sessionId, out var session) || session.AppState == state)
            {
                return;
            }

            var previousState = session.AppState;
            var effectiveChangedAtUtc = changedAtUtc?.ToUniversalTime() ?? DateTimeOffset.UtcNow;
            session.AppState = state;
            session.AppStateChangedUtc = effectiveChangedAtUtc;
            var lifecycleLabel = state switch
            {
                AppLifecycleState.Foreground => "lifecycle.foreground",
                AppLifecycleState.Background => "lifecycle.background",
                _ => null
            };
            if (lifecycleLabel is not null)
            {
                var eventId = $"{AppStateApplicationEventIdPrefix}{effectiveChangedAtUtc.UtcTicks}:{lifecycleLabel}";
                var seenEventIds = session.GetSeenEventIds("application-events");
                var alreadyCaptured = session.ApplicationEvents.Any(appEvent =>
                    appEvent.CapturedAtUtc.ToUniversalTime().UtcTicks == effectiveChangedAtUtc.UtcTicks
                    && string.Equals(appEvent.Label, lifecycleLabel, StringComparison.OrdinalIgnoreCase));
                if (!alreadyCaptured && seenEventIds.Add(eventId))
                {
                    session.ApplicationEvents.Add(new SessionApplicationEvent(
                        eventId,
                        lifecycleLabel,
                        "Lifecycle",
                        string.Empty,
                        effectiveChangedAtUtc,
                        0));
                    session.MarkApplicationEventsChanged();
                }
            }

            SessionStateMapper.TouchTimelineEnd(session, effectiveChangedAtUtc);
            runtimeEvent = new RuntimeClientAppStateChangedEvent(
                DateTimeOffset.UtcNow,
                session.SessionId,
                session.AppId,
                session.ClientName,
                previousState,
                state,
                effectiveChangedAtUtc);
        }

        if (runtimeEvent is not null)
        {
            PublishRuntimeEvent(runtimeEvent);
        }

        PersistAndBroadcast(sessionId);
    }

    public void AddSessionLog(string sessionId, string message)
    {
        // Session logs are reserved for client-originated entries.
    }

    public void AddSessionLogs(string sessionId, IReadOnlyList<string> messages)
    {
        // Session logs are reserved for client-originated entries.
    }

    public void AddSessionLog(string sessionId, LogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        AddSessionLogs(sessionId, new[] { entry });
    }

    public void AddSessionLogs(string sessionId, IReadOnlyList<LogEntry> entries)
        => AddSessionLogEntries(sessionId, SessionLogStreamIds.AnsightSdk, entries);

    public void EnsureSessionLogStream(string sessionId, SessionLogStream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var streamId = NormalizeLogStreamId(stream.StreamId);
        UpdateSession(sessionId, session =>
        {
            if (session.LogStreams.TryGetValue(streamId, out var existing))
            {
                existing.Kind = NormalizeLogStreamValue(stream.Kind, existing.Kind);
                existing.DisplayName = NormalizeLogStreamValue(stream.DisplayName, existing.DisplayName);
                existing.Status = NormalizeLogStreamValue(stream.Status, existing.Status);
                existing.StartedUtc = stream.StartedUtc?.ToUniversalTime() ?? existing.StartedUtc;
                existing.EndedUtc = stream.EndedUtc?.ToUniversalTime();
                existing.StatusMessage = stream.StatusMessage;
                existing.Metadata = new Dictionary<string, string>(stream.Metadata, StringComparer.Ordinal);
            }
            else
            {
                session.LogStreams[streamId] = new SessionLogStreamState
                {
                    StreamId = streamId,
                    Kind = NormalizeLogStreamValue(stream.Kind, SessionLogStreamKinds.Imported),
                    DisplayName = NormalizeLogStreamValue(stream.DisplayName, streamId),
                    Status = NormalizeLogStreamValue(stream.Status, SessionLogStreamStatuses.Pending),
                    StartedUtc = stream.StartedUtc?.ToUniversalTime(),
                    EndedUtc = stream.EndedUtc?.ToUniversalTime(),
                    StatusMessage = stream.StatusMessage,
                    Metadata = new Dictionary<string, string>(stream.Metadata, StringComparer.Ordinal)
                };
            }

            session.MarkLogsChanged();
        });

        if (stream.Entries.Count > 0)
        {
            AddSessionLogEntries(sessionId, streamId, stream.Entries);
        }
    }

    public void SetSessionLogStreamStatus(
        string sessionId,
        string streamId,
        string status,
        string? statusMessage = null,
        DateTimeOffset? endedUtc = null)
    {
        var normalizedStreamId = NormalizeLogStreamId(streamId);
        UpdateSession(sessionId, session =>
        {
            var stream = GetOrCreateLogStream(session, normalizedStreamId);
            stream.Status = NormalizeLogStreamValue(status, SessionLogStreamStatuses.Completed);
            stream.StatusMessage = statusMessage;
            stream.EndedUtc = endedUtc?.ToUniversalTime() ?? stream.EndedUtc;
            session.MarkLogsChanged();
        }, endedUtc);
    }

    public void AddSessionLogEntries(string sessionId, string streamId, IReadOnlyList<LogEntry> entries)
    {
        var normalizedStreamId = NormalizeLogStreamId(streamId);
        var acceptedEntries = entries
            .Where(entry => entry is not null
                            && !string.IsNullOrWhiteSpace(entry.Message)
                            && (!string.Equals(normalizedStreamId, SessionLogStreamIds.AnsightSdk, StringComparison.Ordinal)
                                || !string.Equals(entry.Source, "Host", StringComparison.OrdinalIgnoreCase)))
            .Select(entry => SessionLogStreams.WithStreamId(entry, normalizedStreamId))
            .ToArray();
        if (acceptedEntries.Length == 0)
        {
            return;
        }

        var latestTimestampUtc = acceptedEntries
            .Select(entry => entry.TimestampUtc.ToUniversalTime())
            .Max();
        var addedEntries = new List<LogEntry>(acceptedEntries.Length);
        var appId = string.Empty;
        var streamEntryCount = 0;
        var totalEntryCount = 0;

        UpdateSession(sessionId, session =>
        {
            var stream = GetOrCreateLogStream(session, normalizedStreamId);
            stream.Status = SessionLogStreamStatuses.Active;
            stream.StatusMessage = null;
            stream.EndedUtc = null;
            stream.StartedUtc ??= acceptedEntries.Min(entry => entry.TimestampUtc.ToUniversalTime());
            var seenEventIds = session.GetSeenEventIds(normalizedStreamId);
            var addedLogCount = 0;
            foreach (var entry in acceptedEntries)
            {
                if (!string.IsNullOrWhiteSpace(entry.EventId)
                    && !seenEventIds.Add(entry.EventId))
                {
                    continue;
                }

                session.Logs.Add(entry);
                stream.Entries.Add(entry);
                session.TotalLogCount++;
                stream.TotalEntryCount++;
                addedEntries.Add(entry);
                addedLogCount++;
            }

            if (addedLogCount > 0)
            {
                TrimRetainedTail(stream.Entries, MaximumRetainedLogStreamEntryCount, LogStreamTrimCount);
                TrimRetainedTail(session.Logs, MaximumRetainedSessionLogCount, SessionLogTrimCount);
                session.MarkLogsChanged();
                appId = session.AppId;
                streamEntryCount = stream.TotalEntryCount;
                totalEntryCount = session.TotalLogCount;
            }
        }, latestTimestampUtc, shouldPersist: false);

        if (addedEntries.Count == 0)
        {
            return;
        }

        var batchEntries = addedEntries.ToArray();
        var batch = new SessionLogBatchEventArgs(
            sessionId,
            appId,
            normalizedStreamId,
            batchEntries,
            streamEntryCount,
            totalEntryCount);
        sessionCaptureStore.QueueLogBatch(batch);
        SessionLogsAdded?.Invoke(this, batch);
    }

    private static SessionLogStreamState GetOrCreateLogStream(SessionState session, string streamId)
    {
        if (session.LogStreams.TryGetValue(streamId, out var stream))
        {
            return stream;
        }

        stream = new SessionLogStreamState
        {
            StreamId = streamId,
            Kind = streamId switch
            {
                SessionLogStreamIds.AnsightSdk => SessionLogStreamKinds.AnsightSdk,
                SessionLogStreamIds.AndroidLogcat => SessionLogStreamKinds.AndroidLogcat,
                SessionLogStreamIds.AppleUnifiedLog => SessionLogStreamKinds.AppleUnifiedLog,
                _ => SessionLogStreamKinds.Imported
            },
            DisplayName = streamId switch
            {
                SessionLogStreamIds.AnsightSdk => "Ansight SDK",
                SessionLogStreamIds.AndroidLogcat => "Android Logcat",
                SessionLogStreamIds.AppleUnifiedLog => "Apple Unified Log",
                _ => streamId
            },
            Status = SessionLogStreamStatuses.Pending
        };
        session.LogStreams[streamId] = stream;
        return stream;
    }

    private static string NormalizeLogStreamId(string? streamId)
        => string.IsNullOrWhiteSpace(streamId) ? SessionLogStreamIds.AnsightSdk : streamId.Trim();

    private static string NormalizeLogStreamValue(string? value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    public void UpdateSessionMetricChannels(string sessionId, IReadOnlyList<SessionMetricChannel> channels)
    {
        if (channels.Count == 0)
        {
            return;
        }

        UpdateSession(sessionId, session =>
        {
            foreach (var channel in channels)
            {
                session.MetricChannels[channel.ChannelId] = channel;
            }

            session.MarkMetricChannelsChanged();
        }, updateTimelineEnd: false);
    }

    public void AddSessionMetrics(string sessionId, IReadOnlyList<SessionMetricSample> metrics, int telemetrySegmentId)
    {
        if (metrics.Count == 0)
        {
            return;
        }

        var latestCapturedAtUtc = metrics
            .Select(metric => metric.CapturedAtUtc.ToUniversalTime())
            .DefaultIfEmpty(DateTimeOffset.MinValue)
            .Max();

        UpdateSession(sessionId, session =>
        {
            var normalizedSegmentId = Math.Max(1, telemetrySegmentId);
            var addedMetrics = new List<SessionMetricSample>(metrics.Count);
            foreach (var metric in metrics)
            {
                var key = new MetricDeduplicationKey(metric.ChannelId, metric.Value, metric.CapturedAtUtc);
                if (!session.SeenMetricKeys.Add(key))
                {
                    continue;
                }

                addedMetrics.Add(new SessionMetricSample
                {
                    ChannelId = metric.ChannelId,
                    Value = metric.Value,
                    CapturedAtUtc = metric.CapturedAtUtc,
                    SegmentId = normalizedSegmentId
                });
            }

            if (session.NextTelemetrySegmentId <= normalizedSegmentId)
            {
                session.NextTelemetrySegmentId = normalizedSegmentId + 1;
            }

            if (addedMetrics.Count > 0)
            {
                session.AddMetrics(addedMetrics);
                session.MarkMetricsChanged();
            }
        }, latestCapturedAtUtc == DateTimeOffset.MinValue ? null : latestCapturedAtUtc);
    }

    public void AddSessionTouches(string sessionId, IReadOnlyList<SessionTouchInputRecord> touches)
    {
        if (touches.Count == 0)
        {
            return;
        }

        var normalizedTouches = NormalizeTouches(touches);
        if (normalizedTouches.Length == 0)
        {
            return;
        }

        var latestCapturedAtUtc = normalizedTouches
            .Select(touch => touch.CapturedAtUtc.ToUniversalTime())
            .DefaultIfEmpty(DateTimeOffset.MinValue)
            .Max();

        UpdateSession(sessionId, session =>
        {
            var addedTouches = new List<SessionTouchInputRecord>(normalizedTouches.Length);
            foreach (var touch in normalizedTouches)
            {
                if (!session.SeenTouchIds.Add(touch.Id))
                {
                    continue;
                }

                addedTouches.Add(touch);
            }

            if (addedTouches.Count > 0)
            {
                session.AddTouches(addedTouches);
                session.MarkTouchesChanged();
            }
        }, latestCapturedAtUtc == DateTimeOffset.MinValue ? null : latestCapturedAtUtc);
    }

    public void AddSessionNetworkRequests(
        string sessionId,
        IReadOnlyList<SessionNetworkRequest> requests)
    {
        if (requests.Count == 0)
        {
            return;
        }

        var normalized = requests
            .Select(SessionNetworkRequestSanitizer.Normalize)
            .Where(static request => request is not null)
            .Cast<SessionNetworkRequest>()
            .OrderBy(static request => request.StartedAtUtc)
            .ThenBy(static request => request.Id, StringComparer.Ordinal)
            .ToArray();
        if (normalized.Length == 0)
        {
            return;
        }

        UpdateSession(sessionId, session =>
        {
            var added = false;
            foreach (var request in normalized)
            {
                if (!session.SeenNetworkRequestIds.Add(request.Id))
                {
                    continue;
                }

                session.NetworkRequests.Add(request);
                added = true;
            }

            if (added)
            {
                session.MarkNetworkRequestsChanged();
            }
        }, normalized.Max(static request => request.CompletedAtUtc));
    }

    public void AddSessionApplicationEvents(
        string sessionId,
        IReadOnlyList<SessionApplicationEvent> events)
    {
        if (events.Count == 0)
        {
            return;
        }

        var latestCapturedAtUtc = events.Max(static appEvent => appEvent.CapturedAtUtc.ToUniversalTime());
        UpdateSession(sessionId, session =>
        {
            var seenEventIds = session.GetSeenEventIds("application-events");
            var added = false;
            foreach (var appEvent in events)
            {
                if (!seenEventIds.Add(appEvent.EventId))
                {
                    continue;
                }

                var matchingSyntheticEventIndex = session.ApplicationEvents.FindIndex(existingEvent =>
                    existingEvent.EventId.StartsWith(AppStateApplicationEventIdPrefix, StringComparison.Ordinal)
                    && existingEvent.CapturedAtUtc.ToUniversalTime().UtcTicks == appEvent.CapturedAtUtc.ToUniversalTime().UtcTicks
                    && string.Equals(existingEvent.Label, appEvent.Label, StringComparison.OrdinalIgnoreCase));
                if (matchingSyntheticEventIndex >= 0)
                {
                    session.ApplicationEvents[matchingSyntheticEventIndex] = appEvent;
                }
                else
                {
                    session.ApplicationEvents.Add(appEvent);
                }

                added = true;
            }

            if (added)
            {
                session.MarkApplicationEventsChanged();
            }
        }, latestCapturedAtUtc);
    }

}
