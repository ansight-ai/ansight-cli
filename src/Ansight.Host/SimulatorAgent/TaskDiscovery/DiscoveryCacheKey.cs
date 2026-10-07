using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Ansight.Host.Runtime.Operations;
using Ansight.Host.Runtime.Operations.Tools.RepositoryTasks;
using Ansight.Host.Runtime;

namespace Ansight.Host.SimulatorAgent.TaskDiscovery;

internal sealed record DiscoveryCacheKey(
    string SessionId,
    string? Query,
    string? Feature,
    string? ToolId,
    string Policy);
