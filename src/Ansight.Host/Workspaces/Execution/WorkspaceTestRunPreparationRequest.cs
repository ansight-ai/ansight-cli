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
}
