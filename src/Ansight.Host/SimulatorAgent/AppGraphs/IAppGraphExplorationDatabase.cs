using System.Text.Json;
using System.Text.Json.Nodes;
using Ansight.Host.AppGraphs;
using Microsoft.Data.Sqlite;

namespace Ansight.Host.SimulatorAgent.AppGraphs;

internal interface IAppGraphExplorationDatabase
{
    void Save(SimulatorAgentAppGraphLiveRun run);

    AppGraphResumeState? LoadLatestIncomplete(
        string sessionId,
        string? appId,
        string graphName);
}
