using Ansight.Host;

namespace Ansight.Cli.Commands.Host;

internal sealed record HostStatusOutput(
    string Schema,
    bool IsRunning,
    int? ProcessId,
    string? ControlPipeName,
    string DataDirectory,
    DateTimeOffset? UpdatedUtc,
    bool IsStale,
    string? ExplorerUrl,
    CompanionAccessStatus? CompanionAccess,
    string? CliVersion,
    long? CliBuildNumber,
    string? CommitSha,
    string? LogFilePath);
