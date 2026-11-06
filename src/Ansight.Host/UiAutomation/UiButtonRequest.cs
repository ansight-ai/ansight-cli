namespace Ansight.Host.UiAutomation;

public sealed record UiButtonRequest(
    string SessionId,
    string DeviceIdentifier,
    string Button,
    string? ApplicationIdentifier = null);
