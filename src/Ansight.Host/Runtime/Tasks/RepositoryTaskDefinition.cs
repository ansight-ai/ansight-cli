using System.Text.Json.Nodes;

namespace Ansight.Host.Runtime.Tasks;

internal sealed record RepositoryTaskDefinition(
    string RepositoryRootPath,
    string ModulePath,
    string TaskId,
    int SchemaVersion,
    string AppId,
    string Title,
    string Description,
    string? Feature,
    IReadOnlyList<string> Keywords,
    JsonObject InputSchema,
    JsonObject? OutputSchema,
    IReadOnlyDictionary<string, RepositoryTaskHostToolDescriptor> DeclaredHostTools,
    TimeSpan Timeout,
    int MaximumActions)
{
    public RepositoryTask ToPublicDefinition()
        => new(
            TaskId,
            SchemaVersion,
            AppId,
            Title,
            Description,
            Feature,
            Keywords,
            InputSchema.DeepClone().AsObject(),
            OutputSchema?.DeepClone().AsObject(),
            DeclaredHostTools.Keys.Order(StringComparer.Ordinal).ToArray(),
            (int)Timeout.TotalSeconds,
            MaximumActions)
        {
            Requires = Requires,
            Enabled = Enabled,
            Platforms = Platforms,
            DeviceKinds = DeviceKinds,
            Frameworks = Frameworks,
            ModulePath = ModulePath
        };

    public ExecutionRequirements? Requires { get; init; }

    public bool Enabled { get; init; } = true;

    public IReadOnlyList<string> Platforms { get; init; } = [];

    public IReadOnlyList<string> DeviceKinds { get; init; } = [];

    public IReadOnlyList<string> Frameworks { get; init; } = [];
}

internal sealed record RepositoryTaskLoadResult(
    IReadOnlyList<RepositoryTaskDefinition> Tasks,
    IReadOnlyList<string> Warnings)
{
    public IReadOnlyList<RepositoryTaskSupportModule> SupportModules { get; init; } = [];
}

internal sealed record RepositoryTaskSupportModule(
    string RelativePath,
    string Source,
    bool IsDeclaration);

internal sealed class RepositoryTaskModuleDefinition
{
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
