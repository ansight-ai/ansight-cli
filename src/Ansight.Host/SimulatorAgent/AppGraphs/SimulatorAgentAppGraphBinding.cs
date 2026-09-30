namespace Ansight.Host.SimulatorAgent;

public sealed record SimulatorAgentAppGraphBinding(
    Guid BindingId,
    int Priority,
    string Mechanism,
    System.Text.Json.Nodes.JsonObject Configuration,
    IReadOnlyList<string> Preconditions,
    IReadOnlyList<string> Postconditions,
    decimal Confidence);
