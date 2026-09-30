using Ansight.Host;

namespace Ansight.Cli.Commands.Host;

internal sealed record HostPairingStartedOutput(
    string Schema,
    int ProcessId,
    string ControlPipeName,
    RuntimeStatusSnapshot Status,
    string? ExplorerUrl,
    string Message,
    PairingInviteSummary Invite,
    string? InviteFilePath,
    string? Duration,
    PairingCodeResult Code,
    CompanionAccessStatus? CompanionAccess = null,
    string? CliVersion = null,
    long? CliBuildNumber = null,
    string? CommitSha = null,
    string? LogFilePath = null);
