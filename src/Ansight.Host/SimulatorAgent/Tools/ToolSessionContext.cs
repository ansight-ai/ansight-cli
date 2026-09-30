using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Ansight.Host.Runtime.Operations;
using Ansight.Host.Runtime.Operations.Tools.RepositoryTasks;
using Ansight.Host.Runtime;

namespace Ansight.Host.SimulatorAgent.Tools;

internal sealed record ToolSessionContext(
    string SessionId,
    string AppId,
    string AppName,
    bool IsLive,
    string AppState,
    SimulatorAgentRunDevice? Device);
