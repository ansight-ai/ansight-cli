using System.Text.Json.Serialization;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ansight.Host.Runtime.RepositoryContracts;
using Ansight.Host.Runtime.Operations.Tools.SessionEvidence;
using Ansight.Host.Runtime.Sanitization;
using Ansight.Host.Runtime.Tasks;
using Ansight.Host.SimulatorAgent;
using Ansight.Host.Workspaces.Execution;
using Ansight.Tools;

namespace Ansight.Host.Replay;

public sealed record LocalTaskExtractionModelPassTrace(
    int Sequence,
    string? ResponseId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWriting)]
    string Model,
    string? ServiceTier,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc,
    long DurationMilliseconds,
    SimulatorAgentTokenUsage Tokens,
    IReadOnlyList<string> FunctionCalls)
{
    public string? Reasoning { get; init; }

    public SimulatorAgentAuditPayload? Context { get; init; }

    public SimulatorAgentAuditPayload? AssistantOutput { get; init; }

    public bool Succeeded { get; init; } = true;

    public string? ErrorMessage { get; init; }
}
