using System.Text.Json.Nodes;
using Ansight.Host.AppGraphs;

namespace Ansight.Host.SimulatorAgent;

public sealed record SimulatorAgentAppGraphLiveTraceEntry(
    long Sequence,
    string Stage,
    string Message,
    int Turn,
    string? ToolName,
    DateTimeOffset OccurredUtc);
