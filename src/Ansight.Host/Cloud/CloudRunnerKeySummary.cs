using Ansight.Host.Trends;

namespace Ansight.Host.Cloud;

public sealed record CloudRunnerKeySummary(
    Guid Id,
    Guid TeamId,
    string AppId,
    string Name,
    string KeyPrefix,
    IReadOnlyList<string> Scopes,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? RevokedAt,
    DateTimeOffset? LastUsedAt);
