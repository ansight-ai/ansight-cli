namespace Ansight.Host.Pairing;

public sealed record PairingOperationResult(
    bool IsSuccess,
    string Message,
    PairingInviteSummary? Invite = null);
