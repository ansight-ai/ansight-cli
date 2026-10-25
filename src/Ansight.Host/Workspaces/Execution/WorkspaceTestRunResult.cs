namespace Ansight.Host.Workspaces.Execution;

public sealed record WorkspaceTestRunResult(
    bool IsSuccess,
    string Message,
    WorkspaceTestDefinition? Test,
    string? SessionId,
    SimulatorAgentRunResult? AgentResult,
    IReadOnlyList<string> MissingSecrets,
    WorkspaceTestTarget? Target = null)
{
    public TestVideoResult? Video { get; init; }

    public Ansight.Host.Runtime.Tasks.RepositoryTaskRunResult? TaskResult { get; init; }

    public SimulatorAgentRunAudit? TaskAudit { get; init; }

    public string? TaskAuditFilePath { get; init; }

    public string? TaskAuditPersistenceError { get; init; }

    [System.Text.Json.Serialization.JsonIgnore]
    public SimulatorAgentRunAudit? RunAudit => AgentResult?.Audit ?? TaskAudit;

    public bool WasCancelled => AgentResult?.Status == SimulatorAgentRunStatus.Cancelled
        || TaskResult?.Status == Ansight.Host.Runtime.Tasks.RepositoryTaskRunStatus.Cancelled;

    public string? AppiumSessionId { get; init; }

    public bool IsSkipped { get; init; }

    public static WorkspaceTestRunResult Failure(
        string message,
        WorkspaceTestDefinition? test = null,
        string? sessionId = null,
        IReadOnlyList<string>? missingSecrets = null,
        WorkspaceTestTarget? target = null)
        => new(false, message, test, sessionId, null, missingSecrets ?? [], target);

    public static WorkspaceTestRunResult Skipped(
        string message,
        WorkspaceTestDefinition test,
        string? sessionId = null,
        WorkspaceTestTarget? target = null)
        => new(false, message, test, sessionId, null, [], target)
        {
            IsSkipped = true
        };
}
