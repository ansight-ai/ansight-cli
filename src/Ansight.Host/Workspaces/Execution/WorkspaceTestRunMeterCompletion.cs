namespace Ansight.Host.Workspaces.Execution;

public sealed record WorkspaceTestRunMeterCompletion(
    Guid RunId,
    string Status,
    long DurationMilliseconds,
    int InstructionCount,
    int ModelPassCount,
    int AnsightToolCallCount,
    int SuccessfulAnsightToolCallCount,
    SimulatorAgentTokenUsage? Tokens = null,
    IReadOnlyList<SimulatorAgentModelPassUsage>? ModelPasses = null);
