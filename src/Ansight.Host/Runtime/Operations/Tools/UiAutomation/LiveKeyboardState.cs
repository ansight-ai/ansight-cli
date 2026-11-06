using System.Text.Json.Nodes;
using Ansight.Tools;

namespace Ansight.Host.Runtime.Operations.Tools.UiAutomation;

internal static class LiveKeyboardState
{
    private const string KeyboardVisiblePropertyName = "keyboardVisible";

    public static bool TryRead(LiveUiTreeCapture? capture, out bool isOpen)
    {
        if (capture is null)
        {
            isOpen = false;

            return false;
        }

        return TryRead(capture.ToolId, capture.Payload, out isOpen);
    }

    internal static bool TryRead(string toolId, JsonObject payload, out bool isOpen)
    {
        if (!string.Equals(toolId, LiveUiTreeCapture.DeviceAccessibilityToolId, StringComparison.Ordinal)
            || payload[KeyboardVisiblePropertyName] is not JsonValue value
            || !value.TryGetValue<bool>(out isOpen))
        {
            isOpen = false;

            return false;
        }

        return true;
    }
}
