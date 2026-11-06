namespace Ansight.Host.UiAutomation;

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
