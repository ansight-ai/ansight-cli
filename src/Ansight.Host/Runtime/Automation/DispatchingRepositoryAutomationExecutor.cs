namespace Ansight.Host.Runtime.Automation;

internal sealed class DispatchingRepositoryAutomationExecutor : IRepositoryAutomationExecutor
{
    private readonly IRepositoryAutomationExecutor typeScriptExecutor;
    private readonly IRepositoryAutomationExecutor appToolExecutor;

    public DispatchingRepositoryAutomationExecutor(
        IRepositoryAutomationExecutor typeScriptExecutor,
        IRepositoryAutomationExecutor appToolExecutor)
    {
        this.typeScriptExecutor = typeScriptExecutor ?? throw new ArgumentNullException(nameof(typeScriptExecutor));
        this.appToolExecutor = appToolExecutor ?? throw new ArgumentNullException(nameof(appToolExecutor));
    }

    public Task<RepositoryAutomationExecutionResult> ExecuteAsync(
        RepositoryAutomationExecutionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request.Trigger.Automation.ActionKind switch
        {
            RepositoryAutomationActionKind.TypeScript => typeScriptExecutor.ExecuteAsync(request, cancellationToken),
            RepositoryAutomationActionKind.AppTool => appToolExecutor.ExecuteAsync(request, cancellationToken),
            _ => throw new InvalidOperationException(
                $"Unsupported repository automation action kind '{request.Trigger.Automation.ActionKind}'.")
        };
    }
}
