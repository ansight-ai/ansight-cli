using System.Text.Json.Serialization;

namespace Ansight.RemoteSimulator.Core.Input;

public sealed record RemoteKeyEvent(
    [property: JsonPropertyName("udid")] string DeviceUdid,
    [property: JsonPropertyName("usageCode")] uint UsageCode,
    [property: JsonPropertyName("phase")] string Phase)
{
    public RemoteKeyEvent NormalizeAndValidate()
    {
        if (string.IsNullOrWhiteSpace(DeviceUdid))
        {
            throw new ArgumentException("A runtime device identifier is required.", nameof(DeviceUdid));
        }

        if (UsageCode is < 4 or > 231)
        {
            throw new ArgumentOutOfRangeException(nameof(UsageCode), "A USB HID keyboard usage code between 4 and 231 is required.");
        }

        if (string.IsNullOrWhiteSpace(Phase))
        {
            throw new ArgumentException("A keyboard phase is required.", nameof(Phase));
        }

        var normalizedPhase = Phase.Trim().ToLowerInvariant();
        if (normalizedPhase is not ("down" or "up"))
        {
            throw new ArgumentException("Keyboard phase must be down or up.", nameof(Phase));
        }

        return this with
        {
            DeviceUdid = DeviceUdid.Trim(),
            Phase = normalizedPhase,
        };
    }
}
