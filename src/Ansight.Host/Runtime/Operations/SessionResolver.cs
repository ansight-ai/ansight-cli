using System.Text.Json.Nodes;
using Ansight.Host.Runtime.Operations.Tools.DeviceLifecycle;

namespace Ansight.Host.Runtime.Operations;

internal sealed class SessionResolver
{
    private readonly IRuntimeState runtimeState;
    private readonly IAppToolBridge appToolBridge;

    public SessionResolver(IRuntimeState runtimeState, IAppToolBridge appToolBridge)
    {
        this.runtimeState = runtimeState;
        this.appToolBridge = appToolBridge;
    }

    public HashSet<string> GetLiveSessionIds()
        => new(appToolBridge.GetConnectedSessionIds().Concat(runtimeState.GetActiveDeviceSessionIds()), StringComparer.Ordinal);

    public bool IsLive(string sessionId)
        => runtimeState.IsDeviceSessionActive(sessionId) || appToolBridge.IsSessionConnected(sessionId);

    public IEnumerable<AppSessionSnapshot> LoadResolvedSnapshots(IEnumerable<AppSessionSnapshot> snapshots)
    {
        foreach (var snapshot in snapshots)
        {
            if (runtimeState.TryGetSessionSnapshot(snapshot.SessionId, out var resolved) && resolved is not null)
            {
                yield return resolved;
            }
        }
    }

    public bool TryResolveLiveSession(JsonObject? arguments, out AppSessionSnapshot? snapshot, out string error)
        => TryResolveSession(arguments, requireLiveSession: true, out snapshot, out error);

    public bool TryResolveWaitableSession(JsonObject? arguments, out AppSessionSnapshot? snapshot, out string error)
    {
        if (TryResolveLiveSession(arguments, out snapshot, out error))
        {
            return true;
        }

        var sessionId = arguments?["sessionId"]?.GetValue<string>();
        return !string.IsNullOrWhiteSpace(sessionId)
               && TryLoadResolvedSnapshot(sessionId, out snapshot, out error);
    }

    public bool TryResolveSession(JsonObject? arguments, bool requireLiveSession, out AppSessionSnapshot? snapshot, out string error)
    {
        snapshot = null;
        error = string.Empty;

        var sessionId = arguments?["sessionId"]?.GetValue<string>();
        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            if (requireLiveSession && !IsLive(sessionId))
            {
                error = $"Session '{sessionId}' is not currently connected.";
                return false;
            }

            return TryLoadResolvedSnapshot(sessionId, out snapshot, out error);
        }

        var appId = arguments?["appId"]?.GetValue<string>();
        var deviceId = arguments?["deviceId"]?.GetValue<string>();
        var includeHistorical = !requireLiveSession && (arguments?["includeHistorical"]?.GetValue<bool>() ?? true);
        var liveSessionIds = GetLiveSessionIds();
        var candidateSnapshots = requireLiveSession
            ? GetLiveSessionSnapshots()
            : runtimeState.GetSessionSummaries()
                .Where(item => includeHistorical || liveSessionIds.Contains(item.SessionId))
                .OrderByDescending(item => liveSessionIds.Contains(item.SessionId))
                .ThenByDescending(item => item.LastUpdatedUtc)
                .ToArray();

        if (!string.IsNullOrWhiteSpace(appId) || !string.IsNullOrWhiteSpace(deviceId))
        {
            var matching = candidateSnapshots
                .Where(item => string.IsNullOrWhiteSpace(appId)
                               || string.Equals(item.AppId, appId, StringComparison.Ordinal))
                .Where(item => string.IsNullOrWhiteSpace(deviceId)
                               || string.Equals(
                                   DeviceLifecycleTool.ResolveNativeDeviceIdentifier(item),
                                   deviceId.Trim(),
                                   StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (matching.Length == 0)
            {
                error = BuildNoMatchingSessionError(
                    appId,
                    deviceId,
                    requireLiveSession);
                return false;
            }

            if (matching.Length > 1)
            {
                error = BuildAmbiguousSessionError(appId, deviceId, matching);
                return false;
            }

            return TryLoadResolvedSnapshot(matching[0].SessionId, out snapshot, out error);
        }

        if (candidateSnapshots.Length == 1)
        {
            return TryLoadResolvedSnapshot(candidateSnapshots[0].SessionId, out snapshot, out error);
        }

        error = candidateSnapshots.Length == 0
            ? requireLiveSession
                ? "No live Ansight app sessions are connected."
                : "No captured Ansight sessions are available."
            : $"Multiple matching sessions are available. Provide sessionId or appId. Sessions: {string.Join(", ", candidateSnapshots.Select(item => item.SessionId))}";
        return false;
    }

    private static string BuildNoMatchingSessionError(
        string? appId,
        string? deviceId,
        bool requireLiveSession)
    {
        var source = requireLiveSession ? "live session is connected" : "captured session was found";
        if (!string.IsNullOrWhiteSpace(appId) && !string.IsNullOrWhiteSpace(deviceId))
        {
            return $"No {source} for app '{appId}' on device '{deviceId.Trim()}'.";
        }

        return !string.IsNullOrWhiteSpace(appId)
            ? $"No {source} for app '{appId}'."
            : $"No {source} on device '{deviceId!.Trim()}'.";
    }

    private static string BuildAmbiguousSessionError(
        string? appId,
        string? deviceId,
        IReadOnlyCollection<AppSessionSnapshot> matching)
    {
        var sessions = string.Join(", ", matching.Select(item => item.SessionId));
        if (!string.IsNullOrWhiteSpace(appId) && !string.IsNullOrWhiteSpace(deviceId))
        {
            return $"App '{appId}' has multiple matching sessions on device '{deviceId.Trim()}'. Provide sessionId instead. Sessions: {sessions}";
        }

        if (!string.IsNullOrWhiteSpace(appId))
        {
            return $"App '{appId}' has multiple matching sessions. Provide sessionId instead. Sessions: {sessions}";
        }

        return $"Device '{deviceId!.Trim()}' has multiple matching sessions. Provide appId or sessionId. Sessions: {sessions}";
    }

    public bool TryLoadResolvedSnapshot(string sessionId, out AppSessionSnapshot? snapshot, out string error)
    {
        error = string.Empty;
        if (runtimeState.TryGetSessionSnapshot(sessionId, out snapshot) && snapshot is not null)
        {
            return true;
        }

        error = $"Session '{sessionId}' was not found.";
        snapshot = null;
        return false;
    }

    private AppSessionSnapshot[] GetLiveSessionSnapshots()
    {
        return GetLiveSessionIds()
            .Select(sessionId => runtimeState.TryGetSessionSnapshot(sessionId, out var snapshot) ? snapshot : null)
            .Where(snapshot => snapshot is not null)
            .Cast<AppSessionSnapshot>()
            .OrderByDescending(snapshot => snapshot.LastUpdatedUtc)
            .ToArray();
    }
}
