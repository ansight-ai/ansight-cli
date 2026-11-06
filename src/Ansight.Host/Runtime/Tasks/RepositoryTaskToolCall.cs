using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Ansight.Host.Runtime.Tasks;

public sealed record RepositoryTaskToolCall(
    int Sequence,
    string ToolName,
    DateTimeOffset StartedAtUtc,
    long DurationMilliseconds,
    bool IsError,
    string Message)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? CompletedAtUtc { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CorrelationId { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RepositoryTaskCallPayload? Arguments { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RepositoryTaskCallPayload? Result { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<RepositoryTaskToolCall>? ChildCalls { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<RepositoryTaskAssertion>? Assertions { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RepositoryTaskSourceTrace? SourceTrace { get; init; }
}
