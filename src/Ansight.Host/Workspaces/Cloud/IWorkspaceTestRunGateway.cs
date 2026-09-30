namespace Ansight.Host.Workspaces.Cloud;

public interface IWorkspaceTestRunGateway
{
    Task<WorkspaceTestRunPreparation> PrepareAsync(
        WorkspaceTestRunPreparationRequest request,
        CancellationToken cancellationToken = default);

    Task<WorkspaceTestRunMeterResult> CompleteAsync(
        WorkspaceTestRunMeterCompletion completion,
        CancellationToken cancellationToken = default);
}
