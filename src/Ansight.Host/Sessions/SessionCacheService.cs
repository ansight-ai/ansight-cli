using Ansight.Host.Runtime.Operations;

namespace Ansight.Host.Sessions;

public sealed class SessionCacheService
{
    private readonly ISessionReader sessionReader;
    private readonly IAppToolBridge appToolBridge;

    internal SessionCacheService(
        ISessionReader sessionReader,
        IAppToolBridge appToolBridge)
    {
        this.sessionReader = sessionReader ?? throw new ArgumentNullException(nameof(sessionReader));
        this.appToolBridge = appToolBridge ?? throw new ArgumentNullException(nameof(appToolBridge));
    }

    public bool TryGetSizeBytes(string sessionId, out long cacheSizeBytes)
        => sessionReader.TryGetSessionCacheSizeBytes(sessionId, out cacheSizeBytes);

    public bool ExpandSessionCache(string sessionId)
        => sessionReader.ExpandSessionCache(sessionId);

    public int CompactSessionCache(
        int compactionAgeDays,
        DateTimeOffset? nowUtc = null,
        ISet<string>? protectedSessionIds = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(compactionAgeDays, 1);
        return sessionReader.CompactSessionCache(
            compactionAgeDays,
            nowUtc ?? DateTimeOffset.UtcNow,
            protectedSessionIds);
    }

    public SessionCacheCleanupPlan CreateSessionCacheCleanupPlan(
        int retentionDays,
        long maximumCacheSizeBytes,
        DateTimeOffset? nowUtc = null,
        ISet<string>? protectedSessionIds = null)
    {
        var candidates = sessionReader.GetSessionSummaries()
            .Select(session =>
            {
                var cacheSizeBytes = sessionReader.TryGetSessionCacheSizeBytes(session.SessionId, out var storedCacheSizeBytes)
                    ? storedCacheSizeBytes
                    : session.CacheSizeBytes;
                return new SessionCacheCleanupCandidate(
                    session.SessionId,
                    session.AppId,
                    session.Name,
                    session.CreatedUtc,
                    session.LastUpdatedUtc,
                    Math.Max(0, cacheSizeBytes),
                    session.IsPinned,
                    appToolBridge.IsSessionConnected(session.SessionId)
                    || (sessionReader is ISessionLifecycle lifecycle && lifecycle.IsDeviceSessionActive(session.SessionId)));
            })
            .ToArray();

        return SessionCacheCleanupPlanner.BuildPlan(
            candidates,
            retentionDays,
            maximumCacheSizeBytes,
            nowUtc ?? DateTimeOffset.UtcNow,
            protectedSessionIds);
    }
}
