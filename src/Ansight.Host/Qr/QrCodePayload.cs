using System.Text.Json.Serialization;

namespace Ansight.Host.Qr;

public class QrCodePayload
{
    [JsonPropertyName("T")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("P")]
    public string Payload { get; set; } = string.Empty;
}
