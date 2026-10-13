namespace Ansight.Host.Workspaces.Execution;

public sealed record WorkspaceTestRunPreparationRequest(
    Guid? TeamId,
    string WorkspacePath,
    string TestId,
    string TestName,
    string AppId,
    string Model,
    int ValidationAssertionCount,
    int InstructionCount,
    bool IsDefinition = true)
{
    public string Reasoning { get; init; } = AgentReasoningModes.Fast;
    // Only this operation may use an app-scoped session summarisation key.
    public bool IsSessionSummary { get; init; }
}
