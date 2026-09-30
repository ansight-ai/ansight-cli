namespace Ansight.Host.Pairing;

public sealed record PairingInviteDetail(
    PairingInviteSummary Summary,
    string InviteJson);
