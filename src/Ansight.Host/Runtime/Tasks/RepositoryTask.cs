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
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? StartingState { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SuccessState { get; init; }
    public ExecutionRequirements? Requires { get; init; }
    public bool Enabled { get; init; } = true;

    public IReadOnlyList<string> Platforms { get; init; } = [];

    public IReadOnlyList<string> DeviceKinds { get; init; } = [];

    public IReadOnlyList<string> Frameworks { get; init; } = [];

    public string ModulePath { get; init; } = string.Empty;
}
