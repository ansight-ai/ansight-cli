
namespace Ansight.Host.Replay;

public sealed record LocalSessionCachePlanRequest(
    int RetentionDays,
    long MaximumCacheSizeBytes);
