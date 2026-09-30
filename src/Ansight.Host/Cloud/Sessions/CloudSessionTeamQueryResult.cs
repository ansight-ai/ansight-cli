namespace Ansight.Host.Cloud;

public readonly record struct CloudSessionTeamQueryResult(
    bool IsSuccess,
    string Message,
    IReadOnlyList<CloudSessionTeam> Teams)
{
    public static CloudSessionTeamQueryResult Success(IReadOnlyList<CloudSessionTeam> teams)
        => new(true, string.Empty, teams);

    public static CloudSessionTeamQueryResult Failure(string message)
        => new(false, string.IsNullOrWhiteSpace(message) ? "Unable to load teams." : message.Trim(), Array.Empty<CloudSessionTeam>());
}
