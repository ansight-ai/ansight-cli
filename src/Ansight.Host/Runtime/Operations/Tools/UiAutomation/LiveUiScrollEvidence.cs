using System.Text.Json.Nodes;

namespace Ansight.Host.Runtime.Operations.Tools.UiAutomation;

internal sealed record LiveUiScrollEvidence(
    LiveUiTreeCapture Capture,
    LiveUiNodeMatch? Target,
    LiveUiSelector Selector)
{
    public static async Task<LiveUiScrollEvidence> PrepareAsync(
        LiveUiTreeCapture capture,
        LiveUiNodeMatch? target,
        LiveUiSelector selector,
        IAppToolBridge bridge,
        UiInputRouter inputRouter,
        string operationName,
        string? correlationId,
        CancellationToken cancellationToken)
    {
        var original = new LiveUiScrollEvidence(capture, target, selector);
        var automationId = target is null ? null : LiveUiNodeQuery.ReadAutomationId(target.Node);
        var canEnrich = capture.ToolId != "ui.get_visual_tree"
            && automationId is not null
            && capture.Session.CaptureSource != "device";
        if (!canEnrich)
        {
            return original;
        }

        var observation = LiveUiScrollObservation.Compare(capture, capture, target, selector);
        if (capture.ToolId == LiveUiTreeCapture.DeviceAccessibilityToolId
            && observation["status"]?.GetValue<string>() != "unknown")
        {
            return original;
        }

        // Enrich evidence only. The action still uses the original, freshly validated target.
        var nativeSelector = LiveUiSelector.Parse(new JsonObject
        {
            ["automationId"] = automationId,
            ["visible"] = true
        });
        var result = await LiveUiTreeCapture.CaptureNativeForSelectorAsync(
            capture.Session, bridge, inputRouter, operationName, correlationId,
            nativeSelector, cancellationToken).ConfigureAwait(false);
        if (result.Capture is not { } native || native.ToolId == capture.ToolId)
        {
            return original;
        }

        var matches = LiveUiNodeQuery.Find(native.Root, nativeSelector, native.TypeRegistry);
        var matchesOriginalTarget = matches.Count == 1
            && SameVisibleArea(target!, capture.Viewport, matches[0], native.Viewport)
            && LiveUiScrollObservation.Compare(native, native, matches[0], nativeSelector)["status"]?.GetValue<string>() != "unknown";
        if (!matchesOriginalTarget)
        {
            return original;
        }

        return new LiveUiScrollEvidence(native, matches[0], nativeSelector);
    }

    internal static bool SameVisibleArea(
        LiveUiNodeMatch first, LiveUiBounds? firstViewport,
        LiveUiNodeMatch second, LiveUiBounds? secondViewport)
    {
        var missingGeometry = firstViewport is null
            || secondViewport is null
            || firstViewport.Width <= 0
            || firstViewport.Height <= 0
            || secondViewport.Width <= 0
            || secondViewport.Height <= 0;
        if (missingGeometry)
        {
            return false;
        }

        var firstBounds = LiveUiNodeQuery.ReadBounds(first.Node);
        var secondBounds = LiveUiNodeQuery.ReadBounds(second.Node);
        if (firstBounds is null || secondBounds is null)
        {
            return false;
        }

        var sameArea = Math.Abs((firstBounds.X - firstViewport!.X) / firstViewport.Width - (secondBounds.X - secondViewport!.X) / secondViewport.Width) <= 0.02
            && Math.Abs((firstBounds.Y - firstViewport.Y) / firstViewport.Height - (secondBounds.Y - secondViewport.Y) / secondViewport.Height) <= 0.02
            && Math.Abs(firstBounds.Width / firstViewport.Width - secondBounds.Width / secondViewport.Width) <= 0.02
            && Math.Abs(firstBounds.Height / firstViewport.Height - secondBounds.Height / secondViewport.Height) <= 0.02;
        return sameArea;
    }
}
