using System.Text.Json.Nodes;

namespace Ansight.Host.Runtime.Tasks;

internal sealed class RepositoryTaskModuleDefinition
{
    public string? StartingState { get; init; }
    public string? SuccessState { get; init; }
    public ExecutionRequirements? Requires { get; init; }
    public int? SchemaVersion { get; init; }
    public bool? Enabled { get; init; }
    public string? AppId { get; init; }
    public string? Title { get; init; }
    public string? Description { get; init; }
    public string? Feature { get; init; }
    public List<string> Keywords { get; init; } = [];

    public JsonObject? InputSchema { get; init; }
    public JsonObject? OutputSchema { get; init; }
    public List<string> HostTools { get; init; } = [];

    public List<string>? Platforms { get; init; }
    public List<string>? DeviceKinds { get; init; }
    public List<string>? Frameworks { get; init; }
    public int? TimeoutSeconds { get; init; }
    public int? MaximumActions { get; init; }
}
