namespace Ansight.Host.Runtime.Automation;

internal sealed class DispatchingRepositoryAutomationExecutor : IRepositoryAutomationExecutor
{
    private readonly IRepositoryAutomationExecutor typeScriptExecutor;
    private readonly IRepositoryAutomationExecutor appToolExecutor;
    private readonly SessionActivityService? activity;

    public DispatchingRepositoryAutomationExecutor(
        IRepositoryAutomationExecutor typeScriptExecutor,
        IRepositoryAutomationExecutor appToolExecutor,
        SessionActivityService? activity = null)
    {
        this.typeScriptExecutor = typeScriptExecutor ?? throw new ArgumentNullException(nameof(typeScriptExecutor));
        this.appToolExecutor = appToolExecutor ?? throw new ArgumentNullException(nameof(appToolExecutor));
        this.activity = activity;
    }

    public async Task<RepositoryAutomationExecutionResult> ExecuteAsync(
        RepositoryAutomationExecutionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        // Queued SDK events do not authenticate a human initiator. Never inherit the
        // context of the command that happened to start this background worker.
        using var eventContext = SessionActivityContext.Push(new(new ActivityCaller { Ingress = "runner", ClientName = "ansight-host" }));
        using var caller = SessionActivityContext.ExecuteAs("automation", request.Trigger.Automation.AutomationId, request.RunId);
        return activity is null
            ? await ExecuteCoreAsync(request, cancellationToken).ConfigureAwait(false)
            : await activity.RecordAsync("automation." + request.Trigger.Automation.AutomationId, "automation", request.Event.SessionId,
                () => ExecuteCoreAsync(request, cancellationToken), result => result.Status switch
                {
                    AutomationRunStatus.Succeeded => "succeeded", AutomationRunStatus.Cancelled => "cancelled",
                    AutomationRunStatus.Rejected => "rejected", _ => "failed"
                }, request.Event.CorrelationId, request.Event.AppId).ConfigureAwait(false);
    }

    private Task<RepositoryAutomationExecutionResult> ExecuteCoreAsync(
        RepositoryAutomationExecutionRequest request, CancellationToken cancellationToken)
    {
        return request.Trigger.Automation.ActionKind switch
        {
            RepositoryAutomationActionKind.TypeScript => typeScriptExecutor.ExecuteAsync(request, cancellationToken),
            RepositoryAutomationActionKind.AppTool => appToolExecutor.ExecuteAsync(request, cancellationToken),
            _ => throw new InvalidOperationException(
                $"Unsupported repository automation action kind '{request.Trigger.Automation.ActionKind}'.")
        };
    }
}
