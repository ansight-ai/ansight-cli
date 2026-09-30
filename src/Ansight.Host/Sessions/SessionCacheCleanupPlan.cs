namespace Ansight.Host.Sessions;

public sealed record SessionCacheCleanupPlan(
    int RetentionDays,
    long MaximumCacheSizeBytes,
    long TotalCacheSizeBytes,
    long ProjectedCacheSizeBytes,
    int SessionCount,
    int PinnedSessionCount,
    int LiveSessionCount,
    int RetentionCandidateCount,
    int CacheCandidateCount,
    IReadOnlyList<SessionCacheCleanupItem> Items)
{
    public int DeleteCount => Items.Count;
}
