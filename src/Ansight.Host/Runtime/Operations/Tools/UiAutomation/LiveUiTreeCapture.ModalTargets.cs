using System.Text.Json.Nodes;

namespace Ansight.Host.Runtime.Operations.Tools.UiAutomation;

internal sealed partial class LiveUiTreeCapture
{
    public static async Task<LiveUiCaptureResult> CaptureNativeForSelectorAsync(
        AppSessionSnapshot session, IAppToolBridge appToolBridge, UiInputRouter uiInputRouter,
        string operationName, string? correlationId, LiveUiSelector selector,
        CancellationToken cancellationToken)
    {
        var deviceResult = await CaptureDeviceAccessibilityAsync(session, uiInputRouter,
            cancellationToken: cancellationToken, allowCached: false).ConfigureAwait(false);
        LiveUiCaptureResult nativeResult;
        if (deviceResult.IsSuccess && HasModalDominance(deviceResult))
        {
            nativeResult = await CaptureModalTargetAsync(deviceResult, session, appToolBridge,
                operationName, correlationId, selector, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            nativeResult = await CaptureNativeVisualTreeAsync(session, appToolBridge,
                operationName, correlationId, cancellationToken).ConfigureAwait(false);
            nativeResult = ValidateForegroundTargets(deviceResult, nativeResult, selector);
        }

        return await ExpandNativeScrollTargetAsync(nativeResult, appToolBridge,
            operationName, correlationId, selector, cancellationToken).ConfigureAwait(false);
    }

    private static LiveUiCaptureResult ValidateForegroundTargets(
        LiveUiCaptureResult deviceResult, LiveUiCaptureResult sdkResult, LiveUiSelector? selector)
    {
        var missingContext = deviceResult.Capture is null
            || sdkResult.Capture is null
            || selector is null
            || !selector.HasCriteria;
        if (missingContext)
        {
            return sdkResult;
        }

        var device = deviceResult.Capture!;
        var sdk = sdkResult.Capture!;
        // Flattened native sheets can expose ordinary buttons without modal markers.
        // Corroborate the target against the actual foreground accessibility control.
        var accessibleControls = LiveUiNodeQuery.Enumerate(device.Root, device.TypeRegistry)
            .Where(match =>
            {
                var acceptsInput = LiveUiNodeQuery.IsEffectivelyVisible(match)
                    && LiveUiNodeQuery.IsEffectivelyEnabled(match)
                    && LiveUiNodeQuery.ReadSupportedActions(match.Node, match.TypeRegistry)
                        .Any(action => action is "tap" or "typeText");
                return acceptsInput;
            })
            .ToArray();
        foreach (var target in LiveUiNodeQuery.Find(sdk.Root, selector!, sdk.TypeRegistry))
        {
            if (NormalizeModalTargetBounds(target, sdk.Viewport) is not { } bounds)
            {
                continue;
            }

            var centerX = bounds.X + bounds.Width / 2;
            var centerY = bounds.Y + bounds.Height / 2;
            var foreground = accessibleControls
                .Where(control =>
                {
                    if (NormalizeModalTargetBounds(control, device.Viewport) is not { } hit)
                    {
                        return false;
                    }

                    var containsPoint = centerX >= hit.X
                        && centerX <= hit.X + hit.Width
                        && centerY >= hit.Y
                        && centerY <= hit.Y + hit.Height;
                    return containsPoint;
                })
                .OrderBy(control =>
                {
                    var hit = NormalizeModalTargetBounds(control, device.Viewport)!;
                    return hit.Width * hit.Height;
                })
                .FirstOrDefault();
            if (foreground is null)
            {
                continue;
            }

            var foregroundId = LiveUiNodeQuery.ReadAutomationId(foreground.Node);
            var foregroundText = LiveUiNodeQuery.ReadText(foreground.Node);
            if (string.IsNullOrWhiteSpace(foregroundId) && string.IsNullOrWhiteSpace(foregroundText))
            {
                continue;
            }

            var corroboratedSubtree = LiveUiNodeQuery.Enumerate(target.Node, sdk.TypeRegistry).Any(candidate =>
            {
                var sameId = !string.IsNullOrWhiteSpace(foregroundId)
                    && string.Equals(foregroundId, LiveUiNodeQuery.ReadAutomationId(candidate.Node), StringComparison.Ordinal);
                var sameText = !string.IsNullOrWhiteSpace(foregroundText)
                    && string.Equals(foregroundText, LiveUiNodeQuery.ReadText(candidate.Node), StringComparison.Ordinal);
                var sameIdentity = sameId || sameText;
                return LiveUiNodeQuery.IsEffectivelyVisible(candidate) && sameIdentity;
            });
            // A rendered label can sit inside an accessible button whose label also
            // includes a badge. Its enclosing stable ID proves they are one control.
            // Never use an unrelated sibling or ancestor text alone for this check.
            var corroboratedAncestor = !string.IsNullOrWhiteSpace(foregroundId)
                && target.Ancestors.Any(ancestor => string.Equals(
                    foregroundId, LiveUiNodeQuery.ReadAutomationId(ancestor), StringComparison.Ordinal));
            if (!corroboratedSubtree && !corroboratedAncestor)
            {
                return LiveUiCaptureResult.Failure(
                    "The SDK target is covered by a different foreground accessibility control. No input was delivered. "
                    + "Inspect fresh device accessibility and dismiss or act on the foreground surface before retrying.");
            }
        }

        return sdkResult;
    }

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
        var mayResolveScrollContainer = operationName is "ansight_scroll_ui" or "ansight_swipe_ui"
            || selector.Action == "scroll" || selector.Role == "scrollview";
        var isStructuralInspection = operationName is "ansight_find_ui" or "ansight_wait_for_ui" or "ansight_assert_ui";
        var lacksModalEvidence = accessibleTargets.Length == 0
            && !mayResolveScrollContainer
            && !isStructuralInspection;
        if (lacksModalEvidence)
        {
            return deviceResult;
        }
        var nativeResult = await CaptureNativeVisualTreeAsync(
            session, appToolBridge, operationName, correlationId, cancellationToken).ConfigureAwait(false);
        string[] attemptedToolIds = [DeviceAccessibilityToolId, .. nativeResult.AttemptedToolIds];
        if (nativeResult.Capture is { } native)
        {
            var matches = LiveUiNodeQuery.Find(native.Root, selector, native.TypeRegistry);
            // Check every match so ordering/index selection cannot expose a different,
            // uncorroborated target. Never relax or remove the requested ancestor.
            var usedAccessibilityNodes = new HashSet<JsonObject>();
            var allTargetsCorroborated = selector.IsAvailable(matches.Count) && matches.All(match =>
            {
                var actions = LiveUiNodeQuery.ReadSupportedActions(match.Node, match.TypeRegistry);
                var isScrollTarget = mayResolveScrollContainer && actions.Contains("scroll");
                var isStructuralTarget = isStructuralInspection
                    && !actions.Any(action => action is "tap" or "typeText");
                var canUseDescendants = isScrollTarget || isStructuralTarget;
                return IsCorroboratedModalTarget(match, native, device, accessibleTargets, usedAccessibilityNodes)
                    || canUseDescendants && IsCorroboratedContainer(match, native, device);
            });
            if (allTargetsCorroborated)
            {
                return AddAttemptedToolIds(ValidateForegroundTargets(deviceResult, nativeResult, selector), attemptedToolIds);
            }
        }

        return AddAttemptedToolIds(deviceResult, attemptedToolIds);
    }

    private static bool IsCorroboratedContainer(
        LiveUiNodeMatch target, LiveUiTreeCapture native, LiveUiTreeCapture device)
    {
        if (!LiveUiNodeQuery.IsEffectivelyVisible(target) || !LiveUiNodeQuery.IsEffectivelyEnabled(target))
        {
            return false;
        }

        if (NormalizeModalTargetBounds(target, native.Viewport) is not { } containerBounds)
        {
            return false;
        }

        // Accessibility often flattens a sheet, exposing controls but not their
        // structural containers. Require two distinct visible descendants to agree
        // in identity and geometry; an uncorroborated background list stays blocked.
        var accessible = LiveUiNodeQuery.Enumerate(device.Root, device.TypeRegistry)
            .Where(match => LiveUiNodeQuery.IsEffectivelyVisible(match)).ToArray();
        var usedNodes = new HashSet<JsonObject>();
        foreach (var child in LiveUiNodeQuery.Enumerate(target.Node, native.TypeRegistry))
        {
            if (NormalizeModalTargetBounds(child, native.Viewport) is not { } bounds)
            {
                continue;
            }

            var isContainedVisibleChild = !ReferenceEquals(child.Node, target.Node)
                && LiveUiNodeQuery.IsEffectivelyVisible(child)
                && bounds.X >= containerBounds.X
                && bounds.Y >= containerBounds.Y
                && bounds.X + bounds.Width <= containerBounds.X + containerBounds.Width
                && bounds.Y + bounds.Height <= containerBounds.Y + containerBounds.Height;
            if (!isContainedVisibleChild)
            {
                continue;
            }

            var matches = accessible.Where(candidate =>
            {
                var corroboratesChild = !usedNodes.Contains(candidate.Node)
                    && HasSameModalTargetIdentity(child, candidate)
                    && NormalizeModalTargetBounds(candidate, device.Viewport) is { } candidateBounds
                    && HaveMatchingModalBounds(bounds, candidateBounds);
                return corroboratesChild;
            }).Take(2).ToArray();
            if (matches.Length == 1)
            {
                usedNodes.Add(matches[0].Node);
            }

            if (usedNodes.Count >= 2)
            {
                return true;
            }
        }
        return false;
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
