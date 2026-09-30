namespace Ansight.Host.Workspaces.Cloud;

/// <summary>Prepares local AI runs by default; cloud metering is explicitly selected.</summary>
internal sealed class LocalWorkspaceTestRunGateway : IWorkspaceTestRunGateway
{
    private readonly Func<IWorkspaceTestRunGateway> cloudGateway;
    private readonly Func<string?> resolveApiKey;
    private readonly Func<bool> useCloud;

    public LocalWorkspaceTestRunGateway(Func<IWorkspaceTestRunGateway> cloudGateway)
        : this(cloudGateway, LocalModelAccess.ResolveApiKey, () => LocalModelAccess.IsCloudSelected) { }

    internal LocalWorkspaceTestRunGateway(Func<IWorkspaceTestRunGateway> cloudGateway,
        Func<string?> resolveApiKey, Func<bool> useCloud)
    {
        this.cloudGateway = cloudGateway;
        this.resolveApiKey = resolveApiKey;
        this.useCloud = useCloud;
    }

    public Task<WorkspaceTestRunPreparation> PrepareAsync(WorkspaceTestRunPreparationRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (useCloud()) return cloudGateway().PrepareAsync(request, cancellationToken);
        var apiKey = resolveApiKey()?.Trim();
        return Task.FromResult(string.IsNullOrWhiteSpace(apiKey)
            ? WorkspaceTestRunPreparation.Failure(LocalModelAccess.ConfigurationMessage)
            : WorkspaceTestRunPreparation.Local() with
            {
                ApiKey = apiKey,
                ReasoningConfiguration = AgentReasoningConfiguration.CreateDefault(request.Reasoning, request.Model)
            });
    }

    // Completion is only invoked for preparations with a cloud tracking run ID.
    public Task<WorkspaceTestRunMeterResult> CompleteAsync(WorkspaceTestRunMeterCompletion completion,
        CancellationToken cancellationToken = default) => cloudGateway().CompleteAsync(completion, cancellationToken);
}
