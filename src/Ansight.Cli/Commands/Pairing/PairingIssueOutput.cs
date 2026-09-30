using Ansight.Host;

namespace Ansight.Cli.Commands.Pairing;

internal sealed record PairingIssueOutput(
    string Schema,
    bool IsSuccess,
    string Message,
    PairingInviteSummary? Invite,
    string? InviteFilePath,
    string? Duration,
    PairingCodeResult? Code,
    PairingQrResult? Qr);
