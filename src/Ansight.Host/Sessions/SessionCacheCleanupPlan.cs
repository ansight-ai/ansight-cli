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
    public bool AutoCleanupEnabled { get; init; }
    public DateTimeOffset? LastAutoCleanupUtc { get; init; }
    public int LastAutoCleanupDeletedCount { get; init; }
}
