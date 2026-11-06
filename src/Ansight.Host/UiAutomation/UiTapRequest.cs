namespace Ansight.Host.UiAutomation;

public sealed record UiTapRequest(
    string SessionId,
    string DeviceIdentifier,
    double NormalizedX,
    double NormalizedY,
    string? ApplicationIdentifier = null);
