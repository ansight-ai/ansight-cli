using System.Text.Json;
using System.Text.Json.Nodes;
using Ansight.Host.AppGraphs;
using Microsoft.Data.Sqlite;

namespace Ansight.Host.SimulatorAgent.AppGraphs;

internal sealed record AppGraphResumeState(
    string RunId,
    IReadOnlyList<SimulatorAgentAppGraphLiveNode> Nodes,
    IReadOnlyList<SimulatorAgentAppGraphLiveEdge> Edges,
    IReadOnlyList<SimulatorAgentAppGraphLiveNavigationHost> NavigationHosts,
    IReadOnlyList<SimulatorAgentAppGraphLiveTabGroup> TabGroups,
    IReadOnlyList<SimulatorAgentAppGraphLiveAction> Actions);
