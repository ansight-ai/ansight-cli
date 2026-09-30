using System.Text.Json.Serialization;

namespace Ansight.RemoteSimulator.Core.Input;

public sealed record RemotePointerEvent(
    [property: JsonPropertyName("udid")] string DeviceUdid,
    [property: JsonPropertyName("phase")] string Phase,
    [property: JsonPropertyName("x")] double NormalizedX,
    [property: JsonPropertyName("y")] double NormalizedY,
    [property: JsonPropertyName("pointerId")] long PointerId,
    [property: JsonPropertyName("timestamp")] long TimestampMilliseconds,
    [property: JsonPropertyName("secondary")] RemoteTouchContact? SecondaryContact = null,
    [property: JsonPropertyName("mouseButton")] string? MouseButton = null,
    [property: JsonPropertyName("scrollDeltaX")] double ScrollDeltaX = 0,
    [property: JsonPropertyName("scrollDeltaY")] double ScrollDeltaY = 0)
{
    public RemotePointerEvent NormalizeAndValidate()
    {
        if (string.IsNullOrWhiteSpace(DeviceUdid))
        {
            throw new ArgumentException("A runtime device identifier is required.", nameof(DeviceUdid));
        }

        if (string.IsNullOrWhiteSpace(Phase))
        {
            throw new ArgumentException("A pointer phase is required.", nameof(Phase));
        }

        var normalizedPhase = Phase.Trim().ToLowerInvariant();
        if (normalizedPhase is not ("down" or "move" or "up" or "cancel" or "scroll"))
        {
            throw new ArgumentException("Pointer phase must be down, move, up, cancel, or scroll.", nameof(Phase));
        }

        if (!double.IsFinite(NormalizedX)
            || !double.IsFinite(NormalizedY)
            || !double.IsFinite(ScrollDeltaX)
            || !double.IsFinite(ScrollDeltaY))
        {
            throw new ArgumentException("Pointer coordinates and scroll deltas must be finite.");
        }

        var normalizedMouseButton = string.IsNullOrWhiteSpace(MouseButton)
            ? (normalizedPhase is "down" or "up" or "cancel" ? "left" : "none")
            : MouseButton.Trim().ToLowerInvariant();
        if (normalizedMouseButton is not ("none" or "left" or "right" or "middle"))
        {
            throw new ArgumentException("Mouse button must be none, left, right, or middle.", nameof(MouseButton));
        }

        var normalizedSecondaryContact = SecondaryContact?.NormalizeAndValidate();

        return this with
        {
            DeviceUdid = DeviceUdid.Trim(),
            Phase = normalizedPhase,
            NormalizedX = Math.Clamp(NormalizedX, 0, 1),
            NormalizedY = Math.Clamp(NormalizedY, 0, 1),
            SecondaryContact = normalizedSecondaryContact,
            MouseButton = normalizedMouseButton,
        };
    }
}
