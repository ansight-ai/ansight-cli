namespace Ansight.Host.Workspaces.Execution;

public sealed record WorkspaceTestRunProgress(
    string Stage,
    string Message,
    SimulatorAgentProgress? AgentProgress = null);
