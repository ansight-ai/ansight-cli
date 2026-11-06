namespace Ansight.Host.UiAutomation;

public sealed record UiInputPreflightRequest(
    string SessionId,
    string DeviceIdentifier,
    string? ApplicationIdentifier = null);
