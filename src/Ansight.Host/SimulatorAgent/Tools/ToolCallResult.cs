using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Ansight.Host.Runtime.Operations;
using Ansight.Host.Runtime.Operations.Tools.RepositoryTasks;
using Ansight.Host.Runtime;
using Ansight.Host.Runtime.Tasks;

namespace Ansight.Host.SimulatorAgent.Tools;

internal sealed record ToolCallResult(
    bool IsError,
    string Output,
    string Message)
{
    public string? ModelOutput { get; init; }

    public IReadOnlyList<RepositoryTaskToolCall>? TaskCalls { get; init; }

    public RepositoryTaskSourceTrace? TaskSource { get; init; }

    public JsonObject? TraceEvidence { get; init; }

    public JsonObject? AccessibilityEvidence { get; init; }
}
