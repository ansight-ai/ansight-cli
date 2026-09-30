using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Ansight.Host.SimulatorAgent.OpenAi;

internal sealed record OpenAiTurn(
    string? ResponseId,
    string? ResponseModel,
    JsonArray Output,
    IReadOnlyList<OpenAiFunctionCall> FunctionCalls,
    string AssistantText,
    SimulatorAgentTokenUsage Tokens)
{
    public string? ResponseServiceTier { get; init; }

    public SimulatorAgentTransportDiagnostics? Transport { get; init; }
}
