namespace Ansight.Host.SimulatorAgent;

public sealed record SimulatorAgentStartupStep(
    string Name,
    DateTimeOffset StartedUtc,
    long DurationMilliseconds,
    string Status);
