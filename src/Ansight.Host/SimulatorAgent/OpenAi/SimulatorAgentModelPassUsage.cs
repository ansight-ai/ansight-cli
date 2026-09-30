using System.Text.Json.Serialization;
namespace Ansight.Host.SimulatorAgent;

public sealed record SimulatorAgentModelPassUsage(
    string ResponseId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWriting)]
    string Model,
    string? ServiceTier,
    DateTimeOffset CompletedAtUtc,
    SimulatorAgentTokenUsage Tokens);
