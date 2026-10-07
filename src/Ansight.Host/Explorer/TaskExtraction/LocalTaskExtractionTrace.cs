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

public sealed record LocalTaskExtractionTrace(
    string Status,
    string? Message,
    Guid? RunId,
    SimulatorAgentTokenUsage Tokens,
    SimulatorAgentRunCost? CalculatedCost,
    IReadOnlyList<LocalTaskExtractionModelPassTrace> ModelPasses)
{
    public IReadOnlyList<LocalTaskExtractionToolCallTrace> ToolCalls { get; init; } = [];
}

public sealed record LocalTaskExtractionToolCallTrace(
    int Sequence,
    int PassSequence,
    string CallId,
    string ToolName,
    DateTimeOffset StartedAtUtc,
    long DurationMilliseconds,
    SimulatorAgentAuditPayload Arguments,
    SimulatorAgentAuditPayload Result,
    bool IsError);
