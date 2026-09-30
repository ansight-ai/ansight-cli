using Ansight.Host;

namespace Ansight.Cli.Commands.Pairing;

internal sealed record PairingListOutput(
    string Schema,
    IReadOnlyList<PairingInviteSummary> Invites);
