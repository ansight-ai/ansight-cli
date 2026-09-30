using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ansight.Host.Companion;

public sealed record CompanionMachine(
    Guid Id,
    Guid? TeamId,
    [property: JsonPropertyName("hostId")] string HostId,
    string HostApplication,
    string DisplayName,
    string Platform,
    JsonElement Capabilities,
    string HardwareFingerprint,
    string HardwareFingerprintVersion,
    string AuthorizationStatus,
    DateTimeOffset LastSeenAt,
    DateTimeOffset? RevokedAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    bool IsCurrentMachine,
    bool IsOnline);
