using System.Text.Json.Nodes;

namespace Ansight.Host.Runtime.Tasks;

internal sealed record RepositoryTaskExecutionRequest(
    string RunId,
    RepositoryTaskDefinition Task,
    string SessionId,
    JsonObject Input,
    string CorrelationId)
{
    internal string? BatchRunId { get; init; }
    internal JsonObject? Capabilities { get; init; }
    internal IReadOnlyDictionary<string, string> SecretValues { get; init; } = new Dictionary<string, string>();

    internal Ansight.Host.Runtime.Operations.OperationExecutionContext? OperationContext { get; init; }
    public IReadOnlyList<string> TaskCallChain { get; init; } = [Task.TaskId];

    public bool CaptureTrace { get; init; }
    internal RepositoryTaskSourceCapture? SourceCapture { get; init; }
}
