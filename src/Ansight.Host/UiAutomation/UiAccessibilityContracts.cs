using System.Text.Json.Nodes;

namespace Ansight.Host.UiAutomation;

public sealed record UiAccessibilityRequest(
    string SessionId,
    string DeviceIdentifier,
    string ApplicationIdentifier,
    int MaxNodes = 256,
    int MaxDepth = 32,
    bool AllowCached = true);

public sealed record UiAccessibilityResult(
    bool IsSuccess,
    string Backend,
    string Message,
    JsonObject? Payload)
{
    public static UiAccessibilityResult Failure(string message, string backend = "")
        => new(false, backend, message, null);
}
