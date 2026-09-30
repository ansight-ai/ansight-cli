namespace Ansight.Host.Pairing;

public sealed record PairingCodeResult(
    bool IsSuccess,
    string Message,
    string InviteId,
    string? PairingCode,
    IReadOnlyList<string> HostAddresses);
