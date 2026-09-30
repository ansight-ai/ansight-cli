namespace Ansight.Host.Identity;

public sealed record IdentityInfo(
    string HostName,
    string HostId,
    string Fingerprint,
    string PublicKeyBase64);
