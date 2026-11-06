using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Ansight.Host.Runtime.Tasks;

public sealed record RepositoryTaskRunResult(
    string RunId,
    string RepositoryRootPath,
    string AppId,
    string SessionId,
    string TaskId,
    RepositoryTaskRunStatus Status,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc,
    long DurationMilliseconds,
    string Message,
    JsonObject Input,
    JsonNode? Output,
    IReadOnlyList<RepositoryTaskAssertion> Assertions,
    IReadOnlyList<RepositoryTaskToolCall> ToolCalls,
    string StandardError)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RepositoryTaskSourceTrace? SourceTrace { get; init; }
    public string? TraceId { get; init; }
    public string? OperationId { get; init; }
}
