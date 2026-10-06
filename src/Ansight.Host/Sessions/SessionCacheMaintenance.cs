using Ansight.Host.Runtime;

namespace Ansight.Host.Sessions;

internal static class SessionCacheMaintenance
{
    internal static SessionCacheMaintenanceResult RunOnce(RuntimeCoordinator runtime, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        var maximumBytes = runtime.UserPreferences.SessionAutoCleanupMaximumCacheBytes;
        if (!runtime.UserPreferences.SessionAutoCleanupEnabled)
        {
            return new SessionCacheMaintenanceResult(DateTimeOffset.UtcNow, 0, maximumBytes, 0, 0);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var compactedCount = runtime.SessionCache.CompactSessionCache(
            runtime.UserPreferences.SessionAutoCompactionAgeDays);
        var plan = runtime.SessionCache.CreateSessionCacheCleanupPlan(
            runtime.UserPreferences.SessionAutoCleanupRetentionDays,
            maximumBytes,
            includeRetentionCandidates: false);
        var deletedCount = 0;
        // Age alone does not trigger automatic deletion. Keep recordings until the cache exceeds its limit.
        if (plan.TotalCacheSizeBytes > maximumBytes)
        {
            foreach (var item in plan.Items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var current = runtime.Sessions.GetSummaries().FirstOrDefault(session =>
                    string.Equals(session.SessionId, item.SessionId, StringComparison.Ordinal));
                if (current is null || current.IsPinned || runtime.IsSessionLive(item.SessionId))
                {
                    continue;
                }

                if (runtime.SessionEditing.Delete(item.SessionId).IsSuccess)
                {
                    deletedCount++;
                }
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        var remaining = runtime.SessionCache.CreateSessionCacheCleanupPlan(
            runtime.UserPreferences.SessionAutoCleanupRetentionDays,
            maximumBytes);
        return new SessionCacheMaintenanceResult(
            DateTimeOffset.UtcNow,
            remaining.TotalCacheSizeBytes,
            maximumBytes,
            compactedCount,
            deletedCount);
    }
}
