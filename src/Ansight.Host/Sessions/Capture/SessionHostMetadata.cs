namespace Ansight.Host.Models.Session;

public sealed record SessionHostMetadata
{
    public string Schema { get; init; } = "ansight.capture-host/v1";
    public DateTimeOffset CapturedAtUtc { get; init; }
    public DateTimeOffset? CollectionCompletedUtc { get; init; }
    public string Status { get; init; } = "collecting";
    public string? TargetPlatform { get; init; }
    public string? HostApplication { get; init; }
    public string? CliVersion { get; init; }
    public string? CliInformationalVersion { get; init; }
    public string? CliBuildNumber { get; init; }
    public string? CommitSha { get; init; }
    public string? HostBuildId { get; init; }
    public int ProcessorCount { get; init; }
    public bool? RepositoryAutomationsEnabled { get; init; }
    public string? OsType { get; init; }
    public string? OsVersion { get; init; }
    public string? OsBuild { get; init; }
    public string? OsDescription { get; init; }
    public string? OsArchitecture { get; init; }
    public string? ProcessArchitecture { get; init; }
    public string? DotNetRuntime { get; init; }
    public IReadOnlyList<SessionHostToolMetadata> Tools { get; init; } = [];
}

public sealed record SessionHostToolMetadata(
    string Name,
    string Status,
    string? Version = null,
    string? Build = null,
    string? Path = null,
    string? SelectedVia = null);
