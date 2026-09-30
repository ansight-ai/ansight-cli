namespace Ansight.Host.UiAutomation;

public sealed record UiInputAvailability(
    bool IsAvailable,
    string Backend,
    string Message)
{
    public static UiInputAvailability Unavailable(string message)
        => new(false, string.Empty, message);
}

public sealed record UiInputResult(
    bool IsSuccess,
    string Backend,
    string Message)
{
    public static UiInputResult Failure(string message)
        => new(false, string.Empty, message);
}

public sealed record UiInputPreflightRequest(
    string SessionId,
    string DeviceIdentifier,
    string? ApplicationIdentifier = null);

public sealed record UiTapRequest(
    string SessionId,
    string DeviceIdentifier,
    double NormalizedX,
    double NormalizedY,
    string? ApplicationIdentifier = null);

public sealed record UiSwipeRequest(
    string SessionId,
    string DeviceIdentifier,
    double StartNormalizedX,
    double StartNormalizedY,
    double EndNormalizedX,
    double EndNormalizedY,
    int DurationMilliseconds,
    string? ApplicationIdentifier = null);

public sealed record UiPinchRequest(
    string SessionId,
    string DeviceIdentifier,
    double PrimaryStartNormalizedX,
    double PrimaryStartNormalizedY,
    double SecondaryStartNormalizedX,
    double SecondaryStartNormalizedY,
    double PrimaryEndNormalizedX,
    double PrimaryEndNormalizedY,
    double SecondaryEndNormalizedX,
    double SecondaryEndNormalizedY,
    int DurationMilliseconds,
    string? ApplicationIdentifier = null);

public sealed record UiTextRequest(
    string SessionId,
    string DeviceIdentifier,
    string Text,
    bool ReplaceExisting = true,
    string? ApplicationIdentifier = null);

public sealed record UiButtonRequest(
    string SessionId,
    string DeviceIdentifier,
    string Button,
    string? ApplicationIdentifier = null);
