namespace Ansight.Host.Sessions;

public sealed record SessionCacheMaintenanceResult(
    DateTimeOffset CompletedUtc,
    long CacheSizeBytes,
    long MaximumCacheSizeBytes,
    int CompactedSessionCount,
    int DeletedSessionCount)
{
    public bool IsApproachingLimit => MaximumCacheSizeBytes > 0
        && CacheSizeBytes >= MaximumCacheSizeBytes * 8 / 10;
}
