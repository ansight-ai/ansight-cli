using Ansight.Host;

namespace Ansight.Cli.Commands.Host;

internal sealed record HostStartedOutput(
    string Schema,
    int ProcessId,
    string ControlPipeName,
    RuntimeStatusSnapshot Status,
    string? ExplorerUrl,
    CompanionAccessStatus? CompanionAccess = null,
    string? CliVersion = null,
    long? CliBuildNumber = null,
    string? CommitSha = null,
    string? LogFilePath = null);
