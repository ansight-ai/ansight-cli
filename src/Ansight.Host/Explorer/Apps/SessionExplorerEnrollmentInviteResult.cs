namespace Ansight.Host.Replay;

public sealed record SessionExplorerEnrollmentInviteResult(
    bool IsSuccess,
    string Message,
    PairingInviteSummary? Invite,
    string? Duration,
    string? PairingCode,
    IReadOnlyList<string> HostAddresses,
    string? QrImageDataUrl);
