using Ansight.Host;

namespace Ansight.Cli.Commands.Pairing;

internal sealed record PairingDetailOutput(
    string Schema,
    string InviteId,
    bool IsFound,
    PairingInviteSummary? Invite);
