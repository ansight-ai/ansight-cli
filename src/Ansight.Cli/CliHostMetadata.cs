using Ansight.Host;

namespace Ansight.Cli;

internal sealed record CliHostMetadata(
    int ProcessId,
    DateTimeOffset? ProcessStartedUtc,
    bool IsRunning,
    string? ControlPipeName,
    string DataDirectory,
    DateTimeOffset UpdatedUtc,
    string? ExplorerUrl = null,
    CompanionAccessStatus? CompanionAccess = null,
    string? CliVersion = null,
    long? CliBuildNumber = null,
    string? CommitSha = null,
    string? LogFilePath = null);
