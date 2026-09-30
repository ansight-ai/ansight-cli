using System.Text.Json.Serialization;

namespace Ansight.RemoteSimulator.Core.Input;

public sealed record RemoteTextEvent(
    [property: JsonPropertyName("udid")] string DeviceUdid,
    [property: JsonPropertyName("text")] string Text)
{
    public const int MaximumLength = 4096;

    public RemoteTextEvent NormalizeAndValidate()
    {
        if (string.IsNullOrWhiteSpace(DeviceUdid))
        {
            throw new ArgumentException("A runtime device identifier is required.", nameof(DeviceUdid));
        }

        if (Text is null)
        {
            throw new ArgumentNullException(nameof(Text));
        }

        if (Text.Length > MaximumLength)
        {
            throw new ArgumentException($"Text input cannot exceed {MaximumLength} characters.", nameof(Text));
        }

        return this with
        {
            DeviceUdid = DeviceUdid.Trim(),
        };
    }
}
