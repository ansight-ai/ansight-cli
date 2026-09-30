namespace Ansight.Host.SimulatorAgent;

public sealed record SimulatorAgentProgress(
    SimulatorAgentProgressStage Stage,
    string Message,
    int InstructionIndex,
    int InstructionCount,
    int Turn,
    string? ToolName = null)
{
    public SimulatorAgentRepositoryTaskDiscoveryTrace? TaskDiscovery { get; init; }
}
