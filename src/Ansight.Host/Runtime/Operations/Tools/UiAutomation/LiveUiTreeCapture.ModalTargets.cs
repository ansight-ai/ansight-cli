using System.Text.Json.Nodes;

namespace Ansight.Host.Runtime.Operations.Tools.UiAutomation;

internal sealed partial class LiveUiTreeCapture
{
    private static async Task<LiveUiCaptureResult> CaptureModalTargetAsync(
        LiveUiCaptureResult deviceResult,
        AppSessionSnapshot session,
        IAppToolBridge appToolBridge,
        string operationName,
        string? correlationId,
        LiveUiSelector selector,
        CancellationToken cancellationToken)
    {
        var device = deviceResult.Capture!;
        // Accessibility can flatten a sheet's containers. Use it to establish that the
        // target is on screen, then use the native hierarchy to check the FULL selector.
        // Framework page trees may still describe the page behind the sheet.
        var identitySelector = LiveUiSelector.Parse(new JsonObject
        {
            ["automationId"] = selector.AutomationId,
            ["text"] = selector.Text,
            ["matchMode"] = selector.MatchMode.ToString().ToLowerInvariant(),
            ["caseSensitive"] = selector.CaseSensitive,
            ["visible"] = true,
            ["enabled"] = selector.Enabled
        });
        var accessibleTargets = LiveUiNodeQuery.Find(device.Root, identitySelector, device.TypeRegistry)
            .Where(match => device.Viewport is not null
                            && LiveUiNodeQuery.HasUsableBoundsInViewport(match, device.Viewport))
            .ToArray();
        if (accessibleTargets.Length == 0) return deviceResult;

        var nativeResult = await CaptureNativeVisualTreeAsync(
            session, appToolBridge, operationName, correlationId, cancellationToken).ConfigureAwait(false);
        string[] attemptedToolIds = [DeviceAccessibilityToolId, .. nativeResult.AttemptedToolIds];
        if (nativeResult.Capture is { } native)
        {
            var matches = LiveUiNodeQuery.Find(native.Root, selector, native.TypeRegistry);
            // Check every match so ordering/index selection cannot expose a different,
            // uncorroborated target. Never relax or remove the requested ancestor.
            var usedAccessibilityNodes = new HashSet<JsonObject>();
            if (selector.IsAvailable(matches.Count) && matches.All(match =>
                IsCorroboratedModalTarget(match, native, device, accessibleTargets, usedAccessibilityNodes)))
            {
                return AddAttemptedToolIds(nativeResult, attemptedToolIds);
            }
        }

        return AddAttemptedToolIds(deviceResult, attemptedToolIds);
    }

    private static bool IsCorroboratedModalTarget(
        LiveUiNodeMatch nativeTarget,
        LiveUiTreeCapture native,
        LiveUiTreeCapture device,
        IReadOnlyList<LiveUiNodeMatch> accessibleTargets,
        HashSet<JsonObject> usedAccessibilityNodes)
    {
        if (!LiveUiNodeQuery.IsEffectivelyVisible(nativeTarget)) return false;
        var nativeBounds = NormalizeModalTargetBounds(nativeTarget, native.Viewport);
        if (nativeBounds is null) return false;

        var counterparts = accessibleTargets.Where(accessible =>
                HasSameModalTargetIdentity(nativeTarget, accessible)
                && LiveUiNodeQuery.IsEffectivelyEnabled(nativeTarget) == LiveUiNodeQuery.IsEffectivelyEnabled(accessible)
                && NormalizeModalTargetBounds(accessible, device.Viewport) is { } accessibleBounds
                && HaveMatchingModalBounds(nativeBounds, accessibleBounds))
            .Take(2).ToArray();
        return counterparts.Length == 1 && usedAccessibilityNodes.Add(counterparts[0].Node);
    }

    private static bool HasSameModalTargetIdentity(LiveUiNodeMatch native, LiveUiNodeMatch accessible)
    {
        if (LiveUiNodeQuery.ReadAutomationId(native.Node) is { } automationId)
        {
            // Retain the normal Android resource-ID equivalence, but do not fuzzy-match
            // two nodes when deciding whether they describe the same screen target.
            return LiveUiSelector.Parse(new JsonObject
            {
                ["automationId"] = automationId, ["matchMode"] = "exact", ["caseSensitive"] = true
            }).Matches(accessible);
        }

        return LiveUiNodeQuery.ReadText(native.Node) is { Length: > 0 } text
               && string.Equals(text, LiveUiNodeQuery.ReadText(accessible.Node), StringComparison.Ordinal)
               && string.Equals(LiveUiNodeQuery.ReadRole(native.Node, native.TypeRegistry),
                   LiveUiNodeQuery.ReadRole(accessible.Node, accessible.TypeRegistry), StringComparison.Ordinal);
    }

    private static LiveUiBounds? NormalizeModalTargetBounds(LiveUiNodeMatch target, LiveUiBounds? viewport)
    {
        if (viewport is null || !HasFinitePositiveModalBounds(viewport)
            || LiveUiNodeQuery.ReadBounds(target.Node) is not { } bounds
            || !HasFinitePositiveModalBounds(bounds)
            || LiveUiBounds.Intersection(bounds, viewport) is null) return null;

        return new LiveUiBounds(
            (bounds.X - viewport.X) / viewport.Width,
            (bounds.Y - viewport.Y) / viewport.Height,
            bounds.Width / viewport.Width,
            bounds.Height / viewport.Height);
    }

    private static bool HasFinitePositiveModalBounds(LiveUiBounds bounds)
        => double.IsFinite(bounds.X) && double.IsFinite(bounds.Y)
           && double.IsFinite(bounds.Width) && double.IsFinite(bounds.Height)
           && bounds.Width > 0 && bounds.Height > 0;

    private static bool HaveMatchingModalBounds(LiveUiBounds native, LiveUiBounds accessible)
    {
        if (LiveUiBounds.Intersection(native, accessible) is not { } intersection) return false;
        var intersectionArea = intersection.Width * intersection.Height;
        var unionArea = native.Width * native.Height + accessible.Width * accessible.Height - intersectionArea;
        // Coordinate spaces differ (SDK points/pixels versus normalized accessibility).
        // Require substantial agreement, allowing only small rounding/layout differences.
        return intersectionArea / unionArea >= 0.8;
    }
}
