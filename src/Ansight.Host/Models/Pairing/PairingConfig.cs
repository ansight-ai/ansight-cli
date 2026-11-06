namespace Ansight.Host.Models.Pairing;

using System.Text.Json.Serialization;

public sealed class PairingConfig
{
    public const string SchemaName = "ansight.enrollment-invite.v2";
    public const string AnyAppId = "*";
    public const string AnyAppName = "Any Ansight app";

    public required string Schema { get; set; }
    [JsonPropertyName("inviteId")]
    public required string ConfigId { get; set; }
    public required string AppId { get; set; }
    public required string AppName { get; set; }
    public required DateTimeOffset IssuedAt { get; set; }
    public required DateTimeOffset ExpiresAt { get; set; }
    public int MinProtocolVersion { get; set; } = 2;
    public string[] AllowedTransports { get; set; } = [PairingTransportNames.Ws];
    public required PairingHost Host { get; set; }
    public PairingEnrollment? Enrollment { get; set; }

    public static bool TargetsAnyApp(string? appId)
        => string.Equals(appId?.Trim(), AnyAppId, StringComparison.Ordinal);
}
