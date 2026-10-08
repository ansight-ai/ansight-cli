using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Ansight.Host.Runtime.Tasks;

/// <summary>
/// A trusted, named task discovered from an app repository's ansight/tasks directory.
/// </summary>
public sealed record RepositoryTask(
    string TaskId,
    int SchemaVersion,
    string AppId,
    string Title,
    string Description,
    string? Feature,
    IReadOnlyList<string> Keywords,
    JsonObject InputSchema,
    JsonObject? OutputSchema,
    IReadOnlyList<string> DeclaredHostTools,
    int TimeoutSeconds,
    int MaximumActions)
{
    public ExecutionRequirements? Requires { get; init; }

    public bool Enabled { get; init; } = true;

    public IReadOnlyList<string> Platforms { get; init; } = [];

    public IReadOnlyList<string> DeviceKinds { get; init; } = [];

    public IReadOnlyList<string> Frameworks { get; init; } = [];

    public string ModulePath { get; init; } = string.Empty;
}

public enum RepositoryTaskRunStatus
{
    Passed,
    Failed,
    Error,
    Inconclusive,
    TimedOut,
    Cancelled,
    Rejected
}

public sealed record RepositoryTaskAssertion(
    string AssertionId,
    bool Passed,
    string Message,
    JsonNode? Expected,
    JsonNode? Actual)
{
    public string? Matcher { get; init; }

    public DateTimeOffset? CompletedAtUtc { get; init; }
}

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

public sealed record RepositoryTaskCallPayload(
    string Content,
    int OriginalCharacterCount,
    bool WasTruncated,
    string Sha256);

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
}

public sealed record RepositoryTaskSourceModule(
    string Path,
    string Language,
    string Content,
    string Sha256,
    int OriginalCharacterCount,
    bool WasTruncated);

public sealed record RepositoryTaskSourceTrace(
    string TaskId,
    IReadOnlyList<RepositoryTaskSourceModule> Modules,
    string? CaptureError = null);
