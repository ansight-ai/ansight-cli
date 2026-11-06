using System.Text.Json.Nodes;

namespace Ansight.Host.Runtime.Operations.Tools.UiAutomation;

internal static class LiveUiScrollObservation
{
    public static JsonObject Compare(
        LiveUiTreeCapture before,
        LiveUiTreeCapture? after,
        LiveUiNodeMatch? target,
        LiveUiSelector selector,
        bool afterSettled = true)
    {
        if (!afterSettled)
        {
            return Unknown("A stable scroll viewport was not observed within the bounded settling checks. Transient movement does not prove progress or an end boundary.");
        }

        var cannotCompare = after is null
            || before.ToolId != after.ToolId
            || before.Viewport is null
            || after.Viewport is null;

        if (cannotCompare)
        {
            return Unknown("A complete before/after observation from the same provider was unavailable.");
        }

        var beforeRoot = target?.Node ?? before.Root;
        var afterRoot = after!.Root;

        if (target is not null)
        {
            var matches = LiveUiNodeQuery.PrioritizeActionTargets(
                LiveUiNodeQuery.Find(after.Root, selector, after.TypeRegistry), after.Payload, after.Viewport!);

            if (matches.Count <= selector.Index)
            {
                return Unknown("The observed scroll container could not be identified after the gesture.");
            }

            afterRoot = matches[selector.Index].Node;
        }

        var incomplete = !IsCompleteArea(beforeRoot, before.Payload, before.TypeRegistry)
            || !IsCompleteArea(afterRoot, after.Payload, after.TypeRegistry);

        if (incomplete)
        {
            return Unknown("The scroll area's own descendants were truncated; unrelated window truncation is ignored only when this subtree is complete.");
        }

        var observation = CompareVisibleContent(beforeRoot, before.TypeRegistry, before.Viewport!,
            afterRoot, after.TypeRegistry, after.Viewport!);
        observation["provider"] = before.ToolId;

        return observation;
    }

    internal static bool IsCompleteArea(JsonObject root, JsonObject payload, VisualTreeTypeRegistry types)
    {
        var requiresChildCounts = payload["truncated"]?.GetValue<bool>() == true;

        foreach (var match in LiveUiNodeQuery.Enumerate(root, types))
        {
            var node = match.Node;

            if (node["truncated"]?.GetValue<bool>() == true)
            {
                return false;
            }

            if (node["childCount"] is JsonValue count && count.TryGetValue<int>(out var expected))
            {
                if (expected != ((node["children"] as JsonArray)?.Count ?? 0))
                {
                    return false;
                }
            }
            else if (requiresChildCounts)
            {
                return false;
            }
        }

        return true;
    }

    internal static JsonObject CompareVisibleContent(
        JsonObject beforeRoot, VisualTreeTypeRegistry beforeTypes, LiveUiBounds beforeViewport,
        JsonObject afterRoot, VisualTreeTypeRegistry afterTypes, LiveUiBounds afterViewport)
    {
        var before = Fingerprint(beforeRoot, beforeTypes, beforeViewport);
        var after = Fingerprint(afterRoot, afterTypes, afterViewport);

        if (before is null || after is null)
        {
            return Unknown("The scroll area did not expose enough visible semantic content to verify movement.");
        }

        var changed = before.Hash != after.Hash;

        return new JsonObject
        {
            ["status"] = changed ? "changed" : "unchanged",
            ["beforeFingerprint"] = before.Hash,
            ["afterFingerprint"] = after.Hash,
            ["beforeVisibleNodeCount"] = before.NodeCount,
            ["afterVisibleNodeCount"] = after.NodeCount,
            ["message"] = changed
                ? "Visible content or geometry changed inside the scroll area."
                : "Visible content and geometry are unchanged. Do not repeat this scroll direction; inspect boundaries or record an observability gap."
        };
    }

    private static ViewportFingerprint? Fingerprint(
        JsonObject root, VisualTreeTypeRegistry types, LiveUiBounds viewport)
    {
        var area = LiveUiNodeQuery.ReadBounds(root) is { } bounds
            ? LiveUiBounds.Intersection(bounds, viewport)
            : viewport;
        var hasInvalidArea = area is null
            || area.Width <= 0
            || area.Height <= 0;

        if (hasInvalidArea)
        {
            return null;
        }

        var nodes = new JsonArray();

        foreach (var match in LiveUiNodeQuery.Enumerate(root, types))
        {
            var nodeBounds = LiveUiNodeQuery.ReadBounds(match.Node);
            var isVisible = LiveUiNodeQuery.IsEffectivelyVisible(match)
                && nodeBounds is not null
                && LiveUiBounds.Intersection(nodeBounds, area!) is not null;

            if (!isVisible)
            {
                continue;
            }

            var text = LiveUiNodeQuery.ReadText(match.Node);
            var automationId = LiveUiNodeQuery.ReadAutomationId(match.Node);
            var hasSemanticContent = !ReferenceEquals(match.Node, root)
                && (text is not null
                    || automationId is not null
                    || match.Node["value"] is JsonValue
                    || LiveUiNodeQuery.ReadSupportedActions(match.Node, types).Count > 0);

            if (!hasSemanticContent)
            {
                continue;
            }

            nodes.Add(new JsonObject
            {
                ["text"] = text,
                ["automationId"] = automationId,
                ["role"] = LiveUiNodeQuery.ReadRole(match.Node, types),
                ["value"] = match.Node["value"]?.DeepClone(),
                ["selected"] = match.Node["selected"]?.DeepClone(),
                ["checked"] = match.Node["checked"]?.DeepClone(),
                ["bounds"] = new JsonArray(
                    Math.Round((nodeBounds!.X - area!.X) / area.Width, 3),
                    Math.Round((nodeBounds.Y - area.Y) / area.Height, 3),
                    Math.Round(nodeBounds.Width / area.Width, 3),
                    Math.Round(nodeBounds.Height / area.Height, 3))
            });
        }

        return nodes.Count == 0 ? null : new ViewportFingerprint(SessionVisualTreePersistence.ComputeHash(nodes), nodes.Count);
    }

    private static JsonObject Unknown(string message)
    {
        return new JsonObject { ["status"] = "unknown", ["message"] = message };
    }

    private sealed record ViewportFingerprint(string Hash, int NodeCount);
}
