namespace Ansight.Host.SimulatorAgent;

public sealed record SimulatorAgentAppGraphTransition(
    int Index,
    string EdgeId,
    string FromState,
    string ToState,
    string Action,
    IReadOnlyList<string> Postconditions,
    IReadOnlyList<SimulatorAgentAppGraphBinding> Bindings);
