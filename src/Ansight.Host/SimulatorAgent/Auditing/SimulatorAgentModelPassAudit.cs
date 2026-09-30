using System.Text.Json.Serialization;
namespace Ansight.Host.SimulatorAgent;

public sealed record SimulatorAgentModelPassAudit(
    int Sequence,
    int InstructionIndex,
    int InstructionTurn,
    DateTimeOffset StartedUtc,
    long DurationMilliseconds,
    bool Succeeded,
    string? ResponseId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWriting)]
    string? ResponseModel,
    string? AssistantText,
    int FunctionCallCount,
    SimulatorAgentTokenUsage Tokens,
    string? ErrorMessage)
{
    public SimulatorAgentAuditPayload? Context { get; init; }

    public string? ResponseServiceTier { get; init; }

    public SimulatorAgentTransportDiagnostics? Transport { get; init; }
}
