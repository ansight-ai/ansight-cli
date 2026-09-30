using Ansight.Host;

namespace Ansight.Cli.Commands.Session;

internal sealed record SessionCacheCommandOutput(
    string SchemaVersion,
    string Operation,
    bool Applied,
    SessionCacheCleanupPlan Plan,
    long ResultingCacheSizeBytes,
    IReadOnlyList<string> DeletedSessionIds,
    IReadOnlyList<SessionCacheFailureOutput> Failures);
