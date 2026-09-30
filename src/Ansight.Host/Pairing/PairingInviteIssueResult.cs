namespace Ansight.Host.Pairing;

public sealed record PairingInviteIssueResult(
    bool IsSuccess,
    string Message,
    PairingInviteSummary? Invite,
    string? InviteFilePath,
    string? InviteJson,
    string? Duration);
