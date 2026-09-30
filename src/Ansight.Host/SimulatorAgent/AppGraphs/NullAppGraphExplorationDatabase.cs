using System.Text.Json;
using System.Text.Json.Nodes;
using Ansight.Host.AppGraphs;
using Microsoft.Data.Sqlite;

namespace Ansight.Host.SimulatorAgent.AppGraphs;

internal sealed class NullAppGraphExplorationDatabase
    : IAppGraphExplorationDatabase
{
    public static NullAppGraphExplorationDatabase Instance { get; } = new();

    public void Save(SimulatorAgentAppGraphLiveRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
    }

    public AppGraphResumeState? LoadLatestIncomplete(
        string sessionId,
        string? appId,
        string graphName)
        => null;
}
