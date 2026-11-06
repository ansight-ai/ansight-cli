namespace Ansight.Host.Cloud;

public sealed record SessionUrlResult(
    bool IsSuccess,
    string Message,
    string SourceSessionId,
    bool IsShared,
    string? ShareUrl,
    CloudSessionSummary? SharedSession,
    IReadOnlyList<CloudSessionSummary> Matches);
