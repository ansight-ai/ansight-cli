using System.Text.Json.Nodes;
using Ansight.Host.AppGraphs;

namespace Ansight.Host.SimulatorAgent;

public sealed record SimulatorAgentAppGraphLiveAction(
    string Id,
    string DestinationId,
    string ToolName,
    string AutomationId,
    JsonObject Selector,
    string SemanticMeaning,
    string Status,
    int AttemptCount,
    string LastOutcome,
    string? ResultDestinationId,
    DateTimeOffset FirstObservedUtc,
    DateTimeOffset UpdatedUtc);
