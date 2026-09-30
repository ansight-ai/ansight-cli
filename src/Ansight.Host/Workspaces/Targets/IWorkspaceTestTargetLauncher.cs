namespace Ansight.Host.Workspaces.Targets;

internal interface IWorkspaceTestTargetLauncher
{
    Task<WorkspaceTestTargetLaunchResult> LaunchAsync(
        string applicationIdentifier,
        WorkspaceTestTargetRequest? request,
        IProgress<WorkspaceTestRunProgress>? progress,
        CancellationToken cancellationToken);

    Task<DeviceOperationResult> StopAsync(
        WorkspaceTestTarget target,
        CancellationToken cancellationToken);
}
