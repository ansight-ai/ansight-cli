using System.Text.Json.Nodes;

namespace Ansight.Host.UiAutomation;

public sealed record UiAccessibilityRequest(
    string SessionId,
    string DeviceIdentifier,
    string ApplicationIdentifier,
    int MaxNodes = 256,
    int MaxDepth = 32,
    bool AllowCached = true);
