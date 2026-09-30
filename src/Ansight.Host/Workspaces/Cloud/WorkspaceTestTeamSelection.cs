namespace Ansight.Host.Workspaces.Cloud;

internal sealed record WorkspaceTestTeamSelection(
    bool IsSuccess,
    string Message,
    WorkspaceTestTeamMembership? Team)
{
    public static WorkspaceTestTeamSelection Success(WorkspaceTestTeamMembership team) =>
        new(true, string.Empty, team);

    public static WorkspaceTestTeamSelection Failure(string message) =>
        new(false, message.Trim(), null);
}
