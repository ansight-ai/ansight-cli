namespace Ansight.Host.Sessions;

public sealed record SessionCacheCleanupItem(
    string SessionId,
    string AppId,
    string? Name,
    DateTimeOffset CreatedUtc,
    DateTimeOffset LastUpdatedUtc,
    long CacheSizeBytes,
    string Reason);
