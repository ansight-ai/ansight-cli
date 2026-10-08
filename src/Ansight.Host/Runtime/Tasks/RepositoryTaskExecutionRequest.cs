using System.Text.Json.Nodes;

namespace Ansight.Host.Runtime.Tasks;

internal sealed record RepositoryTaskExecutionRequest(
    string RunId,
    RepositoryTaskDefinition Task,
    string SessionId,
    JsonObject Input,
    string CorrelationId)
{
    internal JsonObject? Capabilities { get; init; }

    internal IReadOnlyDictionary<string, string> SecretValues { get; init; } = new Dictionary<string, string>();

    internal Ansight.Host.Runtime.Operations.OperationExecutionContext? OperationContext { get; init; }

    public IReadOnlyList<string> TaskCallChain { get; init; } = [Task.TaskId];

    public bool CaptureTrace { get; init; }

    internal RepositoryTaskSourceCapture? SourceCapture { get; init; }
}

internal delegate Task<global::Ansight.Host.Runtime.Operations.RequestResult> RepositoryTaskToolExecutor(
    string toolName,
    JsonObject arguments,
    string? correlationId);

internal sealed record RepositoryTaskApiResult(
    global::Ansight.Host.Runtime.Operations.RequestResult Result,
    IReadOnlyList<RepositoryTaskToolCall>? ChildCalls = null,
    RepositoryTaskSourceTrace? SourceTrace = null,
    IReadOnlyList<RepositoryTaskAssertion>? Assertions = null);

internal delegate Task<RepositoryTaskApiResult> RepositoryTaskApiExecutor(
    RepositoryTaskExecutionRequest parentRequest,
    string methodName,
    JsonObject arguments,
    string? correlationId,
    CancellationToken cancellationToken);
