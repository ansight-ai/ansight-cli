namespace Ansight.Host.Workspaces.Cloud;

/// <summary>Resolves the cloud extension only when an AI run is prepared or completed.</summary>
internal sealed class CloudWorkspaceTestRunGateway : IWorkspaceTestRunGateway
{
    private readonly Func<IWorkspaceTestRunGateway> cloudGateway;

    public CloudWorkspaceTestRunGateway(Func<IWorkspaceTestRunGateway> cloudGateway)
    {
        this.cloudGateway = cloudGateway;
    }

    public Task<WorkspaceTestRunPreparation> PrepareAsync(WorkspaceTestRunPreparationRequest request,
        CancellationToken cancellationToken = default)
        => cloudGateway().PrepareAsync(request, cancellationToken);

    // Completion is only invoked for preparations with a cloud tracking run ID.
    public Task<WorkspaceTestRunMeterResult> CompleteAsync(WorkspaceTestRunMeterCompletion completion,
        CancellationToken cancellationToken = default) => cloudGateway().CompleteAsync(completion, cancellationToken);
}
