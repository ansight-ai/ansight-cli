namespace Ansight.Host.Cloud;

public sealed record SessionShareResult(
    bool IsSuccess,
    string Message,
    string SessionId,
    string AccessLevel,
    CloudSessionTeam? Team,
    CloudSessionSummary? SharedSession,
    string? ShareUrl,
    IReadOnlyList<CloudSessionTeam> AvailableTeams);
