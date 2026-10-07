namespace Ansight.Host.Explorer;

public sealed record SessionStorageSettings(
    bool SessionAutoCleanupEnabled,
    int SessionAutoCleanupRetentionDays,
    int SessionAutoCompactionAgeDays,
    long SessionAutoCleanupMaximumCacheBytes);
