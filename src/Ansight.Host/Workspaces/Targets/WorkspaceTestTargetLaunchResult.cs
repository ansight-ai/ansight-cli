namespace Ansight.Host.Workspaces.Targets;

internal sealed record WorkspaceTestTargetLaunchResult(
    bool IsSuccess,
    string Message,
    WorkspaceTestTarget? Target = null,
    string? EnrollmentInviteId = null)
{
    public IDisposable? DeviceClaim { get; init; }
    public AppSessionSnapshot? ExistingSession { get; init; }

    public static WorkspaceTestTargetLaunchResult Success(
        WorkspaceTestTarget target,
        string? enrollmentInviteId = null)
        => new(
            true,
            $"Launched '{target.ApplicationIdentifier}' on '{target.DeviceName}'.",
            target,
            enrollmentInviteId);

    public static WorkspaceTestTargetLaunchResult Failure(string message)
        => new(false, message);
}
