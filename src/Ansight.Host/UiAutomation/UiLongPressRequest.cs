using System.Text.Json.Nodes;

namespace Ansight.Host.UiAutomation;

public sealed record UiLongPressRequest(
    string SessionId,
    string DeviceIdentifier,
    double NormalizedX,
    double NormalizedY,
    int DurationMilliseconds = UiLongPressRequest.DefaultDurationMilliseconds,
    string? ApplicationIdentifier = null)
{
    public const int DefaultDurationMilliseconds = 800;
    public const int MinimumDurationMilliseconds = 500;
    public const int MaximumDurationMilliseconds = 10_000;

    internal static bool TryReadDuration(JsonObject? arguments, out int duration, out string error)
    {
        duration = DefaultDurationMilliseconds;
        error = string.Empty;
        if (arguments?["durationMs"] is null) return true;
        if (arguments["durationMs"] is JsonValue value && value.TryGetValue<int>(out duration)
            && duration is >= MinimumDurationMilliseconds and <= MaximumDurationMilliseconds)
            return true;
        error = $"durationMs must be an integer between {MinimumDurationMilliseconds} and {MaximumDurationMilliseconds} for a long press.";
        return false;
    }

    internal void Validate()
    {
        if (!double.IsFinite(NormalizedX) || !double.IsFinite(NormalizedY)
            || NormalizedX is < 0 or > 1 || NormalizedY is < 0 or > 1)
            throw new ArgumentException("Long press requires finite normalized coordinates from 0 through 1.");
        if (DurationMilliseconds is < MinimumDurationMilliseconds or > MaximumDurationMilliseconds)
            throw new ArgumentOutOfRangeException(nameof(DurationMilliseconds),
                $"Long-press duration must be between {MinimumDurationMilliseconds} and {MaximumDurationMilliseconds} milliseconds.");
    }
}
