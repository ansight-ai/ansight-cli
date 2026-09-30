using System.Text.Json.Serialization;

namespace Ansight.RemoteSimulator.Core.Input;

public sealed record RemoteTouchContact(
    [property: JsonPropertyName("x")] double NormalizedX,
    [property: JsonPropertyName("y")] double NormalizedY)
{
    public RemoteTouchContact NormalizeAndValidate()
    {
        if (!double.IsFinite(NormalizedX) || !double.IsFinite(NormalizedY))
        {
            throw new ArgumentException("Secondary touch coordinates must be finite.");
        }

        return this with
        {
            NormalizedX = Math.Clamp(NormalizedX, 0, 1),
            NormalizedY = Math.Clamp(NormalizedY, 0, 1),
        };
    }
}
