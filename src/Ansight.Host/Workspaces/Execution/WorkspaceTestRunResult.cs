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
