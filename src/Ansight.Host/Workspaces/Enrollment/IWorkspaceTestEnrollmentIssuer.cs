namespace Ansight.Host.Workspaces.Enrollment;

internal interface IWorkspaceTestEnrollmentIssuer
{
    UnattendedEnrollmentIssueResult Issue(string appId);

    void RevokeUnconsumed(string? inviteId, string appId);
}
