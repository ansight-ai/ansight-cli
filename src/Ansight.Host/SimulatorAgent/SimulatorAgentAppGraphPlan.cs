namespace Ansight.Host.SimulatorAgent;

public sealed record SimulatorAgentAppGraphPlan(
    Guid GraphId,
    Guid VersionId,
    string Name,
    string Intent,
    string TargetState,
    IReadOnlyList<SimulatorAgentAppGraphTransition> Transitions);
