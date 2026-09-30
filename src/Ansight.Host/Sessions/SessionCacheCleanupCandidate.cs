namespace Ansight.Host.Sessions;

public sealed record SessionCacheCleanupCandidate(
    string SessionId,
    string AppId,
    string? Name,
    DateTimeOffset CreatedUtc,
    DateTimeOffset LastUpdatedUtc,
    long CacheSizeBytes,
    bool IsPinned,
    bool IsLive);
