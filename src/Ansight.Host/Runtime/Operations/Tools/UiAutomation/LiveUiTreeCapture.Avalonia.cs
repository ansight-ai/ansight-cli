using System.Text.Json.Nodes;
using Ansight.Tools;

namespace Ansight.Host.Runtime.Operations.Tools.UiAutomation;

internal sealed partial class LiveUiTreeCapture
{
    private static async Task<LiveUiCaptureResult> CaptureAvaloniaFallbackAsync(
        LiveUiCaptureResult preferred,
        AppSessionSnapshot session,
        IAppToolBridge bridge,
        JsonNode? catalog,
        AppToolBridgeRequestContext context,
        LiveUiSelector selector,
        CancellationToken cancellationToken)
    {
        var selected = await ExpandAvaloniaSelectorAsync(
            preferred, session, bridge, context, selector, cancellationToken).ConfigureAwait(false);
        var attempted = preferred.AttemptedToolIds.ToList();

        foreach (var toolId in new[]
        {
            VisualTreeContract.AvaloniaAccessibilityToolId,
            VisualTreeContract.AvaloniaLogicalToolId,
            VisualTreeContract.AvaloniaVisualToolId
        })
        {
            if (ContainsSelectorEvidence(selected, selector))
            {
                break;
            }

            if (attempted.Contains(toolId) || !RemoteAppToolCatalog.HasTool(catalog, toolId))
            {
                continue;
            }

            attempted.Add(toolId);
            var candidate = await CaptureWithToolAsync(
                session, bridge, toolId, context, cancellationToken).ConfigureAwait(false);
            candidate = await ExpandAvaloniaSelectorAsync(
                candidate, session, bridge, context, selector, cancellationToken).ConfigureAwait(false);

            if (!selected.IsSuccess || ContainsSelectorEvidence(candidate, selector))
            {
                selected = candidate;
            }
        }

        return selected with { AttemptedToolIds = attempted.ToArray() };
    }

    private static async Task<LiveUiCaptureResult> ExpandAvaloniaSelectorAsync(
        LiveUiCaptureResult initial,
        AppSessionSnapshot session,
        IAppToolBridge bridge,
        AppToolBridgeRequestContext context,
        LiveUiSelector selector,
        CancellationToken cancellationToken)
    {
        if (initial.Capture is not { } original || ContainsSelectorEvidence(initial, selector))
        {
            return initial;
        }

        const int maximumPages = 6;
        var pending = new Queue<string>();
        var scheduled = new HashSet<string>(StringComparer.Ordinal);
        var originalRootId = LiveUiNodeQuery.ReadString(original.Root, "id");

        if (originalRootId is not null)
        {
            scheduled.Add(originalRootId);
        }

        EnqueueAvaloniaFrontiers(original, selector, pending, scheduled);

        for (var pageIndex = 0; pageIndex < maximumPages && pending.TryDequeue(out var rootNodeId); pageIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = await CaptureWithToolAsync(
                session, bridge, original.ToolId, context, cancellationToken,
                new JsonObject
                {
                    ["rootNodeId"] = rootNodeId,
                    ["maxDepth"] = 12,
                    ["maxNodes"] = 400
                }).ConfigureAwait(false);

            if (page.Capture is not { } capture
                || LiveUiNodeQuery.ReadString(capture.Root, "id") != rootNodeId)
            {
                continue;
            }

            // Subtree bounds remain absolute to the original TopLevel. A subtree's
            // own rectangle must never become the device-input viewport.
            var anchored = new LiveUiTreeCapture(
                capture.Session, capture.ToolId, capture.RawPayload, capture.Payload,
                capture.Root, capture.TypeRegistry, original.Viewport, capture.CapturedAtUtc);
            page = LiveUiCaptureResult.Success(anchored);

            if (ContainsSelectorEvidence(page, selector))
            {
                return page with { AttemptedToolIds = initial.AttemptedToolIds };
            }

            EnqueueAvaloniaFrontiers(anchored, selector, pending, scheduled);
        }

        return initial;
    }

    private static void EnqueueAvaloniaFrontiers(
        LiveUiTreeCapture capture,
        LiveUiSelector selector,
        Queue<string> pending,
        HashSet<string> scheduled)
    {
        foreach (var match in LiveUiNodeQuery.Enumerate(capture.Root, capture.TypeRegistry))
        {
            var children = match.Node["children"] as JsonArray;
            var childCount = match.Node["childCount"]?.GetValue<int>() ?? children?.Count ?? 0;
            var nodeId = LiveUiNodeQuery.ReadString(match.Node, "id");
            var hasUncapturedChildren = childCount > (children?.Count ?? 0)
                && nodeId is not null
                && !scheduled.Contains(nodeId);

            if (!hasUncapturedChildren)
            {
                continue;
            }

            var hiddenFromVisibleSearch = selector.Visible == true
                && !LiveUiNodeQuery.IsEffectivelyVisible(match);
            var outsideVisibleSearch = selector.Visible == true
                && capture.Viewport is { } viewport
                && LiveUiNodeQuery.ReadBounds(match.Node) is { Width: > 0, Height: > 0 }
                && !LiveUiNodeQuery.HasUsableBoundsInViewport(match, viewport);

            if (hiddenFromVisibleSearch || outsideVisibleSearch)
            {
                continue;
            }

            scheduled.Add(nodeId!);
            pending.Enqueue(nodeId!);
        }
    }
}
