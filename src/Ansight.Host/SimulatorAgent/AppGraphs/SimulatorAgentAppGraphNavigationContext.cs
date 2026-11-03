namespace Ansight.Host.SimulatorAgent;

/// <summary>The caller destination required for an observed return transition.</summary>
public sealed record SimulatorAgentAppGraphNavigationContext(string ReturnDestinationId);
