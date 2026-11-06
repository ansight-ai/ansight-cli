namespace Ansight.Host.Models.Pairing;

public sealed class PairingClientGrant
{
    public required string GrantId { get; set; }
    public required string SecretHash { get; set; }
    public required string HostId { get; set; }
    public required string ConfigId { get; set; }
    public required string AppId { get; set; }
    public required string DeviceId { get; set; }
    public required string DeviceName { get; set; }
    public string MaxToolPolicy { get; set; } = "read";
    public required DateTimeOffset IssuedAt { get; set; }
    public required DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public string? RevocationReason { get; set; }

    public bool IsActive(DateTimeOffset now)
        => RevokedAt is null && now <= ExpiresAt;
}
