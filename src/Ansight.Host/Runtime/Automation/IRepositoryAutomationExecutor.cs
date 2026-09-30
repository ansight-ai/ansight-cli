namespace Ansight.Host.Runtime.Automation;

internal interface IRepositoryAutomationExecutor
{
    Task<RepositoryAutomationExecutionResult> ExecuteAsync(
        RepositoryAutomationExecutionRequest request,
        CancellationToken cancellationToken);
}
