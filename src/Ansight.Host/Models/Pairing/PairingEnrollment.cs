namespace Ansight.Host.Models.Pairing;

using System.Text.Json.Serialization;

public sealed class PairingEnrollment
{
    [JsonPropertyName("accessToken")]
    public required string Secret { get; set; }
    public required DateTimeOffset ExpiresAt { get; set; }
    public required DateTimeOffset GrantExpiresAt { get; set; }
    public int MaxUses { get; set; } = 1;
    public string MaxToolPolicy { get; set; } = "read";
}
