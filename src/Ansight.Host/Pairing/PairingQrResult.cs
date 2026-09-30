namespace Ansight.Host.Pairing;

public sealed record PairingQrResult(
    bool IsSuccess,
    string Message,
    string InviteId,
    string? QrFilePath,
    string? PairingCode,
    IReadOnlyList<string> HostAddresses);
