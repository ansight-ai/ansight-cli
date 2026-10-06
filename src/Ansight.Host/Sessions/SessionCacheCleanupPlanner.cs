namespace Ansight.Host.Sessions;

public static class SessionCacheCleanupPlanner
{
    public static SessionCacheCleanupPlan BuildPlan(
        IReadOnlyList<SessionCacheCleanupCandidate>? candidates,
        int retentionDays,
        long maximumCacheSizeBytes,
        DateTimeOffset nowUtc,
        ISet<string>? protectedSessionIds = null,
        bool includeRetentionCandidates = true)
    {
        candidates ??= Array.Empty<SessionCacheCleanupCandidate>();

        var normalizedRetentionDays = Math.Max(1, retentionDays);
        var normalizedMaximumCacheSizeBytes = Math.Max(0, maximumCacheSizeBytes);
        var cutoffUtc = nowUtc.ToUniversalTime().AddDays(-normalizedRetentionDays);
        var totalCacheSizeBytes = candidates.Sum(static candidate => Math.Max(0, candidate.CacheSizeBytes));
        var projectedCacheSizeBytes = totalCacheSizeBytes;
        var items = new List<SessionCacheCleanupItem>();
        var selectedSessionIds = new HashSet<string>(StringComparer.Ordinal);

        var retentionCandidates = candidates
            .Where(candidate => includeRetentionCandidates
                                && IsDeletable(candidate, protectedSessionIds)
                                && candidate.CreatedUtc < cutoffUtc)
            .OrderBy(static candidate => candidate.CreatedUtc)
            .ThenBy(static candidate => candidate.LastUpdatedUtc)
            .ToArray();

        foreach (var candidate in retentionCandidates)
        {
            if (!selectedSessionIds.Add(candidate.SessionId))
            {
                continue;
            }

            items.Add(ToItem(candidate, "retention"));
            projectedCacheSizeBytes = Math.Max(
                0,
                projectedCacheSizeBytes - Math.Max(0, candidate.CacheSizeBytes));
        }

        var cacheCandidateCount = 0;
        if (projectedCacheSizeBytes > normalizedMaximumCacheSizeBytes)
        {
            foreach (var candidate in candidates
                         .Where(candidate => IsDeletable(candidate, protectedSessionIds)
                                             && candidate.CacheSizeBytes > 0
                                             && !selectedSessionIds.Contains(candidate.SessionId))
                         .OrderBy(static candidate => candidate.CreatedUtc)
                         .ThenBy(static candidate => candidate.LastUpdatedUtc))
            {
                if (projectedCacheSizeBytes <= normalizedMaximumCacheSizeBytes)
                {
                    break;
                }

                if (!selectedSessionIds.Add(candidate.SessionId))
                {
                    continue;
                }

                items.Add(ToItem(candidate, "cache-limit"));
                projectedCacheSizeBytes = Math.Max(0, projectedCacheSizeBytes - candidate.CacheSizeBytes);
                cacheCandidateCount++;
            }
        }

        return new SessionCacheCleanupPlan(
            normalizedRetentionDays,
            normalizedMaximumCacheSizeBytes,
            totalCacheSizeBytes,
            projectedCacheSizeBytes,
            candidates.Count,
            candidates.Count(static candidate => candidate.IsPinned),
            candidates.Count(static candidate => candidate.IsLive),
            retentionCandidates.Length,
            cacheCandidateCount,
            items);
    }

    private static bool IsDeletable(
        SessionCacheCleanupCandidate candidate,
        ISet<string>? protectedSessionIds)
    {
        return !candidate.IsPinned
               && !candidate.IsLive
               && protectedSessionIds?.Contains(candidate.SessionId) != true;
    }

    private static SessionCacheCleanupItem ToItem(
        SessionCacheCleanupCandidate candidate,
        string reason)
    {
        return new SessionCacheCleanupItem(
            candidate.SessionId,
            candidate.AppId,
            candidate.Name,
            candidate.CreatedUtc,
            candidate.LastUpdatedUtc,
            Math.Max(0, candidate.CacheSizeBytes),
            reason);
    }
}
