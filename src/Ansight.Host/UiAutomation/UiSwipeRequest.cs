namespace Ansight.Host.UiAutomation;

public sealed record UiSwipeRequest(
    string SessionId,
    string DeviceIdentifier,
    double StartNormalizedX,
    double StartNormalizedY,
    double EndNormalizedX,
    double EndNormalizedY,
    int DurationMilliseconds,
    string? ApplicationIdentifier = null);
