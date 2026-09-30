namespace Ansight.Host.Workspaces.Batches;

public sealed record WorkspaceTestBatchProgress(
    int TestIndex,
    int TestCount,
    string TestId,
    string Message,
    SimulatorAgentProgress? AgentProgress = null,
    WorkspaceTestRunProgress? TestProgress = null)
{
    public int TargetIndex { get; init; } = 1;

    public int TargetCount { get; init; } = 1;

    public string? DeviceIdentifier { get; init; }
}
