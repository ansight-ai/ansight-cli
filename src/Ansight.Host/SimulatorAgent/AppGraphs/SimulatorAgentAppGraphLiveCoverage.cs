using System.Text.Json.Nodes;
using Ansight.Host.AppGraphs;

namespace Ansight.Host.SimulatorAgent;

public sealed record SimulatorAgentAppGraphLiveCoverage(
    int SafeActionsObserved,
    int ActionsExplored,
    int ScrollContainersObserved,
    int ScrollContainersCompleted,
    IReadOnlyList<string> Gaps)
{
    public static SimulatorAgentAppGraphLiveCoverage Empty { get; } = new(0, 0, 0, 0, []);
}
