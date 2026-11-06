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
            StartingState = StartingState,
            SuccessState = SuccessState,
            Requires = Requires,
            Enabled = Enabled,
            Platforms = Platforms,
            DeviceKinds = DeviceKinds,
            Frameworks = Frameworks,
            ModulePath = ModulePath
        };

    public string? StartingState { get; init; }
    public string? SuccessState { get; init; }
    public ExecutionRequirements? Requires { get; init; }
    public bool Enabled { get; init; } = true;

    public IReadOnlyList<string> Platforms { get; init; } = [];

    public IReadOnlyList<string> DeviceKinds { get; init; } = [];

    public IReadOnlyList<string> Frameworks { get; init; } = [];
}
