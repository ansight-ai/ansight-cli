using System.Text.Json.Nodes;
using Ansight.Host.AppGraphs;

namespace Ansight.Host.SimulatorAgent;

public sealed record SimulatorAgentAppGraphLiveRun(
    string Schema,
    string RunId,
    string SessionId,
    string? AppId,
    string GraphName,
    string Status,
    string Message,
    string? CurrentDestinationId,
    string? ActiveToolName,
    int Turn,
    DateTimeOffset StartedUtc,
    DateTimeOffset UpdatedUtc,
    DateTimeOffset? CompletedUtc,
    SimulatorAgentAppGraphLiveCoverage Coverage,
    IReadOnlyList<SimulatorAgentAppGraphLiveNode> Nodes,
    IReadOnlyList<SimulatorAgentAppGraphLiveEdge> Edges,
    IReadOnlyList<SimulatorAgentAppGraphLiveNavigationHost> NavigationHosts,
    IReadOnlyList<SimulatorAgentAppGraphLiveTabGroup> TabGroups,
    IReadOnlyList<SimulatorAgentAppGraphLiveAction> Frontier,
    IReadOnlyList<SimulatorAgentAppGraphLiveTraceEntry> Trace);
