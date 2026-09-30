using System.Text.Json.Nodes;
using Ansight.Host.Runtime.Operations.Tools.UiAutomation;

namespace Ansight.Host.UiAutomation;

/// <summary>Fresh semantic observations with a cached capability choice, never cached targets.</summary>
internal sealed class AppInteractionTree(
    IRuntimeState runtimeState, IAppToolBridge appToolBridge, UiInputRouter input, AppSessionSnapshot session)
{
    private string? fallbackToolId;
    public AppInteractionUi? Latest { get; private set; }

    public async Task<LiveUiTreeCapture?> CaptureAsync(
        string actionId, AppInteractionTarget? target, CancellationToken cancellationToken,
        PersistedScreenshotEvidence? screenshot = null)
    {
        Latest = null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(2_000);
        try
        {
            var capture = await LiveUiTreeCapture.CaptureForInteractionAsync(session, appToolBridge, input,
                actionId, target is null ? null : Selector(target), fallbackToolId, timeout.Token).ConfigureAwait(false);
            if (!capture.IsSuccess || capture.Capture is not { } tree)
            {
                Latest = Unavailable(capture.Message);
                return null;
            }
            if (tree.ToolId != LiveUiTreeCapture.DeviceAccessibilityToolId) fallbackToolId = tree.ToolId;
            var persisted = SessionVisualTreePersistence.Persist(runtimeState, session, tree.ToolId,
                tree.RawPayload, new(actionId, "interact", target is null ? "after" : "before"), screenshot);
            Latest = Describe(tree, persisted.Snapshot?.SnapshotId);
            return tree;
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            Latest = Unavailable(exception is OperationCanceledException
                ? "Fresh UI tree capture timed out; use the retained screenshot if appropriate."
                : $"Fresh UI tree unavailable: {exception.Message}");
            return null;
        }
    }

    internal static LiveUiSelector Selector(AppInteractionTarget target) => LiveUiSelector.Parse(new JsonObject
    {
        ["automationId"] = target.AutomationId, ["text"] = target.Text, ["role"] = target.Role,
        ["ancestorAutomationId"] = target.AncestorAutomationId, ["exact"] = true,
        ["caseSensitive"] = true, ["visible"] = true, ["enabled"] = true
    });

    internal static AppInteractionUi Describe(LiveUiTreeCapture tree, string? snapshotId)
    {
        var candidates = LiveUiNodeQuery.Enumerate(tree.Root, tree.TypeRegistry)
            .Where(LiveUiNodeQuery.IsEffectivelyVisible)
            .Where(node => tree.Viewport is null || LiveUiNodeQuery.HasUsableBoundsInViewport(node, tree.Viewport))
            .Where(node => LiveUiNodeQuery.ReadAutomationId(node.Node) is not null
                || LiveUiNodeQuery.ReadText(node.Node) is not null
                || LiveUiNodeQuery.ReadSupportedActions(node.Node, tree.TypeRegistry).Count > 0)
            .ToArray();
        var options = UiProjectionOptions.Interaction;
        var projection = UiResultProjection.Project(new JsonObject
        {
            ["matches"] = new JsonArray(candidates
                .Select(node => (JsonNode)UiNodeProjection.FromMatch(node, options)).ToArray())
        }, options);
        var projectedNodes = projection["matches"]!.AsArray().OfType<JsonObject>();
        var truncated = tree.Payload["truncated"]?.GetValue<bool>() == true
                        || projection["projectionTruncated"]?.GetValue<bool>() == true;
        var nodes = projectedNodes.Select(node => new AppInteractionUiNode(
            AutomationId: node["automationId"]?.GetValue<string>(),
            Text: node["text"]?.GetValue<string>(),
            Role: node["role"]!.GetValue<string>(),
            Value: node["value"] is JsonValue value && value.TryGetValue<string>(out var textValue) ? textValue : null,
            Enabled: node["enabled"]?.GetValue<bool>() ?? true,
            Actions: node["supportedActions"]?.AsArray().Select(action => action!.GetValue<string>()).ToArray() ?? [],
            AncestorAutomationIds: node["ancestorPath"]?.AsArray().OfType<JsonObject>()
                .Select(ancestor => ancestor["automationId"]?.GetValue<string>()).OfType<string>().ToArray() ?? []))
            .ToArray();
        return new("available", tree.ToolId, snapshotId, tree.CapturedAtUtc, nodes, truncated);
    }

    private static AppInteractionUi Unavailable(string message) => new("unavailable", null, null, null, [], false, message);
}
