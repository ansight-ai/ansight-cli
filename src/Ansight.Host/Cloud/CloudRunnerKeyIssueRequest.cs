using Ansight.Host.Trends;

namespace Ansight.Host.Cloud;

public sealed record CloudRunnerKeyIssueRequest(
    Guid TeamId,
    string AppId,
    string Name,
    IReadOnlyList<string> Scopes,
    DateTimeOffset? ExpiresAt);
