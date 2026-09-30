using System.Text.Json.Serialization;

namespace Ansight.RemoteSimulator.Core.Input;

public sealed record RemoteButtonEvent(
    [property: JsonPropertyName("udid")] string DeviceUdid,
    [property: JsonPropertyName("button")] string Button)
{
    public RemoteButtonEvent NormalizeAndValidate()
    {
        if (string.IsNullOrWhiteSpace(DeviceUdid))
        {
            throw new ArgumentException("A runtime device identifier is required.", nameof(DeviceUdid));
        }

        if (string.IsNullOrWhiteSpace(Button))
        {
            throw new ArgumentException("A simulator button is required.", nameof(Button));
        }

        var normalizedButton = Button.Trim().ToLowerInvariant();
        if (normalizedButton is not ("home" or "back" or "lock" or "volume-up" or "volume-down"))
        {
            throw new ArgumentException("Button must be home, back, lock, volume-up, or volume-down.", nameof(Button));
        }

        return this with
        {
            DeviceUdid = DeviceUdid.Trim(),
            Button = normalizedButton,
        };
    }
}
