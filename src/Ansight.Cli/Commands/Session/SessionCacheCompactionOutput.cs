namespace Ansight.Cli.Commands.Session;

internal sealed record SessionCacheCompactionOutput(
    string SchemaVersion,
    string Operation,
    int CompactionAgeDays,
    int CompactedSessionCount,
    long ResultingCacheSizeBytes);
