using System.Text.Json.Nodes;
using Ansight.Tools;

namespace Ansight.Host.Runtime.Operations.Tools.UiAutomation;

internal sealed partial class LiveUiTreeCapture
{
    private static async Task<LiveUiCaptureResult> ExpandNativeScrollTargetAsync(
        LiveUiCaptureResult result, IAppToolBridge bridge, string operationName,
        string? correlationId, LiveUiSelector selector, CancellationToken cancellationToken)
    {
        var canExpand = operationName == "ansight_scroll_ui"
            && result.Capture is { ToolId: RemoteAppToolIds.UiGetVisualTree } && selector.HasCriteria;

        if (!canExpand)
        {
            return result;
        }

        var capture = result.Capture!;
        var targets = LiveUiNodeQuery.PrioritizeActionTargets(
            LiveUiNodeQuery.Find(capture.Root, selector, capture.TypeRegistry), capture.Payload, capture.Viewport!);

        if (targets.Count != 1)
        {
            return result;
        }

        var target = targets[0];
        var needsSubtree = !LiveUiScrollObservation.IsCompleteArea(target.Node, capture.Payload, capture.TypeRegistry)
            && target.Node["id"] is JsonValue;

        if (!needsSubtree)
        {
            return result;
        }

        // Resolve the runtime ID in the fresh window above; never reuse it across input.
        var arguments = new JsonObject
        {
            ["source"] = "native",
            ["rootNodeId"] = target.Node["id"]!.DeepClone(),
            ["includeBounds"] = true,
            ["maxNodes"] = 2000,
            ["maxDepth"] = 32
        };
        var response = await bridge.CallToolWithCatalogRecoveryAsync(
            capture.Session.SessionId, RemoteAppToolIds.UiGetVisualTree, arguments, after: null,
            cancellationToken, new AppToolBridgeRequestContext("host-ui", operationName, correlationId)).ConfigureAwait(false);

        if (!response.Success || response.Envelope?.Payload?["result"] is not JsonObject payload)
        {
            return result;
        }

        var expanded = CreateCapture(capture.Session, capture.ToolId, payload.DeepClone().AsObject());

        if (expanded.Capture is not { } subtree)
        {
            return result;
        }

        var matches = LiveUiNodeQuery.Find(subtree.Root, selector, subtree.TypeRegistry);
        var sameTarget = matches.Count == 1
            && LiveUiScrollEvidence.SameVisibleArea(target, capture.Viewport, matches[0], capture.Viewport)
            && LiveUiScrollObservation.IsCompleteArea(matches[0].Node, subtree.Payload, subtree.TypeRegistry);

        if (!sameTarget)
        {
            return result;
        }

        // A subtree's bounds are not the screen viewport. Preserve the window's
        // coordinate space so gesture/evidence geometry stays comparable.
        return LiveUiCaptureResult.Success(new LiveUiTreeCapture(
            subtree.Session, subtree.ToolId, subtree.RawPayload, subtree.Payload,
            subtree.Root, subtree.TypeRegistry, capture.Viewport, subtree.CapturedAtUtc));
    }
}
