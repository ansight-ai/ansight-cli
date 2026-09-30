using System.Text.Json.Nodes;
using Ansight.Host.AppGraphs;

namespace Ansight.Host.SimulatorAgent;

public sealed record SimulatorAgentAppGraphLiveEdge(
    string Id,
    string From,
    string To,
    string AutomationId,
    string SemanticMeaning,
    decimal Confidence,
    DateTimeOffset FirstObservedUtc,
    DateTimeOffset UpdatedUtc);
