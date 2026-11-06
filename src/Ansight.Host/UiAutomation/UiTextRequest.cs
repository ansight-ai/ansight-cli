namespace Ansight.Host.UiAutomation;

public sealed record UiTextRequest(
    string SessionId,
    string DeviceIdentifier,
    string Text,
    bool ReplaceExisting = true,
    string? ApplicationIdentifier = null);
