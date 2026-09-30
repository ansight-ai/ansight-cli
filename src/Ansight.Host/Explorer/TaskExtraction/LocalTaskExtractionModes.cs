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

namespace Ansight.Host.Explorer.TaskExtraction;

internal static class LocalTaskExtractionModes
{
    public const string Hosted = "hosted";
    public const string DirectWebSocket = "directWebSocket";
}
