using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Ansight.Host.Runtime.Operations;
using Ansight.Host.Runtime.Operations.Tools.RepositoryTasks;
using Ansight.Host.Runtime;

namespace Ansight.Host.SimulatorAgent.TaskDiscovery;

internal sealed record RepositoryTaskShortcut(
    string ToolName,
    string TaskId,
    string Title,
    string Description,
    string? Feature,
    JsonObject InputSchema,
    double? Score,
    double? Coverage)
{
    public IReadOnlyList<string> Keywords { get; init; } = [];
}
