using System.Text.Json.Nodes;

namespace Ansight.Host.Runtime.Operations.Tools.UiAutomation;

internal static class LiveUiNodeQuery
{
    private const string AndroidUiEvidenceOverlayType =
        "ai.ansight.runtime.AndroidUiEvidence$OverlaySurface";
    private const string SdkOverlayAutomationId = "ansight.overlay.surface";

    public static IReadOnlyList<LiveUiNodeMatch> Find(
        JsonObject root,
        LiveUiSelector selector,
        VisualTreeTypeRegistry typeRegistry)
    {
        var seenNodeIds = new HashSet<string>(StringComparer.Ordinal);
        return Enumerate(root, typeRegistry)
            .Where(candidate => selector.Matches(candidate))
            .Where(candidate => ReadString(candidate.Node, "id") is not { } nodeId
                                || seenNodeIds.Add(nodeId))
            .ToArray();
    }

    public static IEnumerable<LiveUiNodeMatch> Enumerate(
        JsonObject root,
        VisualTreeTypeRegistry typeRegistry)
    {
        var ancestors = new List<JsonObject>();
        return EnumerateCore(root, ancestors, typeRegistry).ToArray();
    }

    public static IReadOnlyList<LiveUiNodeMatch> PrioritizeActionTargets(
        IReadOnlyList<LiveUiNodeMatch> matches,
        JsonObject payload,
        LiveUiBounds viewport)
        => OrderMatchesForSelection(matches, payload, viewport);

    public static IReadOnlyList<LiveUiNodeMatch> OrderMatchesForSelection(
        IReadOnlyList<LiveUiNodeMatch> matches,
        JsonObject payload,
        LiveUiBounds? viewport)
    {
        if (viewport is null || viewport.Width <= 0 || viewport.Height <= 0)
        {
            return matches;
        }

        var hasActivePageContext = LiveUiMauiContext.HasActivePageContext(payload);
        return matches
            .Select((match, index) => new LiveUiRankedNodeMatch(match, index))
            .OrderByDescending(candidate => IsActionReady(candidate.Match, viewport))
            .ThenByDescending(candidate => hasActivePageContext
                                           && LiveUiMauiContext.IsInActivePage(candidate.Match, payload))
            .ThenByDescending(candidate => HasUsableBoundsInViewport(candidate.Match, viewport))
            .ThenBy(candidate => candidate.Index)
            .Select(candidate => candidate.Match)
            .ToArray();
    }

    public static LiveUiBounds? ReadBounds(JsonObject node)
    {
        if (node["bounds"] is JsonArray boundsArray)
        {
            var offset = boundsArray.Count >= 8 ? 4 : 0;
            if (boundsArray.Count < offset + 4
                || !TryReadDouble(boundsArray, offset, out var arrayX)
                || !TryReadDouble(boundsArray, offset + 1, out var arrayY)
                || !TryReadDouble(boundsArray, offset + 2, out var arrayWidth)
                || !TryReadDouble(boundsArray, offset + 3, out var arrayHeight))
            {
                return null;
            }

            return new LiveUiBounds(arrayX, arrayY, arrayWidth, arrayHeight);
        }

        if (node["bounds"] is not JsonObject bounds)
        {
            return null;
        }

        var hasAbsoluteBounds = bounds["absoluteX"] is not null
                                && bounds["absoluteY"] is not null
                                && bounds["absoluteWidth"] is not null
                                && bounds["absoluteHeight"] is not null;
        var xName = hasAbsoluteBounds ? "absoluteX" : "x";
        var yName = hasAbsoluteBounds ? "absoluteY" : "y";
        var widthName = hasAbsoluteBounds ? "absoluteWidth" : "width";
        var heightName = hasAbsoluteBounds ? "absoluteHeight" : "height";
        return TryReadDouble(bounds, xName, out var x)
               && TryReadDouble(bounds, yName, out var y)
               && TryReadDouble(bounds, widthName, out var width)
               && TryReadDouble(bounds, heightName, out var height)
            ? new LiveUiBounds(x, y, width, height)
            : null;
    }

    public static LiveUiBounds? ReadCoordinateSpace(JsonObject payload)
    {
        if (payload["coordinateSpace"] is not JsonObject coordinateSpace
            || !TryReadDouble(coordinateSpace, "x", out var x)
            || !TryReadDouble(coordinateSpace, "y", out var y)
            || !TryReadDouble(coordinateSpace, "width", out var width)
            || !TryReadDouble(coordinateSpace, "height", out var height))
        {
            return null;
        }

        return new LiveUiBounds(x, y, width, height);
    }

    public static string? ReadAutomationId(JsonObject node)
    {
        return FirstString(
            ReadString(node, "automationId"),
            ReadString(node, "accessibilityIdentifier"),
            ReadString(node, "testID"),
            ReadString(node, "nativeID"),
            ReadNestedString(node, "properties", "accessibilityIdentifier"),
            ReadNestedString(node, "props", "testID"),
            ReadNestedString(node, "props", "nativeID"));
    }

    public static string? ReadText(JsonObject node)
    {
        return FirstString(
            ReadString(node, "text"),
            ReadString(node, "label"),
            ReadString(node, "value"),
            ReadNestedString(node, "visual", "text"),
            ReadNestedString(node, "visual", "value"),
            ReadNestedString(node, "props", "accessibilityLabel"),
            ReadNestedString(node, "props", "title"));
    }

    public static string? ReadValue(JsonObject node)
    {
        return FirstString(
            ReadString(node, "value"),
            ReadNestedString(node, "visual", "value"),
            ReadNestedString(node, "properties", "value"),
            ReadNestedString(node, "props", "value"));
    }

    public static string ReadRole(JsonObject node, VisualTreeTypeRegistry typeRegistry)
    {
        var explicitRole = FirstString(
            ReadString(node, "role"),
            ReadNestedString(node, "props", "accessibilityRole"));
        return explicitRole?.ToLowerInvariant() ?? InferRole(typeRegistry.Resolve(node));
    }

    public static IReadOnlyList<string> ReadSupportedActions(
        JsonObject node,
        VisualTreeTypeRegistry typeRegistry)
    {
        if (node["supportedActions"] is JsonArray actions)
        {
            return actions
                .OfType<JsonValue>()
                .Select(value => value.TryGetValue<string>(out var action) ? Normalize(action) : null)
                .OfType<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        return InferActions(ReadRole(node, typeRegistry));
    }

    public static bool ReadBoolean(
        JsonObject node,
        string propertyName,
        bool fallback,
        VisualTreeTypeRegistry? typeRegistry = null)
    {
        if (node[propertyName] is JsonValue value && value.TryGetValue<bool>(out var result))
        {
            return result;
        }

        return typeRegistry?.ReadState(node, propertyName) ?? fallback;
    }

    public static bool IsEffectivelyVisible(LiveUiNodeMatch match)
    {
        return ReadBoolean(match.Node, "visible", fallback: true, match.TypeRegistry)
               && match.Ancestors.All(ancestor =>
                   ReadBoolean(ancestor, "visible", fallback: true, match.TypeRegistry));
    }

    public static bool IsEffectivelyEnabled(LiveUiNodeMatch match)
    {
        return ReadBoolean(match.Node, "enabled", fallback: true, match.TypeRegistry)
               && match.Ancestors.All(ancestor =>
                   ReadBoolean(ancestor, "enabled", fallback: true, match.TypeRegistry));
    }

    public static bool HasUsableBoundsInViewport(LiveUiNodeMatch match, LiveUiBounds viewport)
        => ReadBounds(match.Node) is { Width: > 0, Height: > 0 } bounds
           && LiveUiBounds.Intersection(bounds, viewport) is not null;

    public static string? ReadString(JsonObject node, string propertyName)
    {
        return node[propertyName] is JsonValue value
               && value.TryGetValue<string>(out var text)
            ? Normalize(text)
            : null;
    }

    public static JsonObject ToResultJson(LiveUiNodeMatch match)
    {
        var result = match.Node.DeepClone() as JsonObject ?? new JsonObject();
        result.Remove("children");
        result["automationId"] = ReadAutomationId(match.Node);
        result["text"] = ReadText(match.Node);
        result["type"] = match.TypeRegistry.Resolve(match.Node);
        result["role"] = ReadRole(match.Node, match.TypeRegistry);
        result["supportedActions"] = new JsonArray(
            ReadSupportedActions(match.Node, match.TypeRegistry).Select(action => JsonValue.Create(action)).ToArray());
        result["visible"] = IsEffectivelyVisible(match);
        result["enabled"] = IsEffectivelyEnabled(match);
        result["bounds"] = ReadBounds(match.Node) is { } bounds
            ? bounds.ToJson()
            : null;
        result["depth"] = match.Ancestors.Count;
        result["ancestorPath"] = new JsonArray(match.Ancestors.Select(ancestor => new JsonObject
        {
            ["id"] = ReadString(ancestor, "id"),
            ["automationId"] = ReadAutomationId(ancestor),
            ["text"] = ReadText(ancestor),
            ["role"] = ReadRole(ancestor, match.TypeRegistry),
            ["type"] = match.TypeRegistry.Resolve(ancestor)
        }).ToArray());
        return result;
    }

    private static IEnumerable<LiveUiNodeMatch> EnumerateCore(
        JsonObject node,
        List<JsonObject> ancestors,
        VisualTreeTypeRegistry typeRegistry)
    {
        if (IsSdkInjectedOverlayNode(node, typeRegistry))
        {
            yield break;
        }

        yield return new LiveUiNodeMatch(node, ancestors.ToArray(), typeRegistry);
        if (node["children"] is not JsonArray children)
        {
            yield break;
        }

        ancestors.Add(node);
        foreach (var child in children.OfType<JsonObject>())
        {
            foreach (var descendant in EnumerateCore(child, ancestors, typeRegistry))
            {
                yield return descendant;
            }
        }

        ancestors.RemoveAt(ancestors.Count - 1);
    }

    private static bool IsSdkInjectedOverlayNode(
        JsonObject node,
        VisualTreeTypeRegistry typeRegistry)
        => string.Equals(
               typeRegistry.Resolve(node),
               AndroidUiEvidenceOverlayType,
               StringComparison.Ordinal)
           || string.Equals(
               ReadAutomationId(node),
               SdkOverlayAutomationId,
               StringComparison.Ordinal);

    private static string? ReadNestedString(JsonObject node, string objectName, string propertyName)
        => node[objectName] is JsonObject nested ? ReadString(nested, propertyName) : null;

    private static bool TryReadDouble(JsonObject node, string propertyName, out double value)
    {
        value = 0;
        if (node[propertyName] is not JsonValue jsonValue)
        {
            return false;
        }

        if (jsonValue.TryGetValue<double>(out value))
        {
            return true;
        }

        if (!jsonValue.TryGetValue<int>(out var integer))
        {
            return false;
        }

        value = integer;
        return true;
    }

    private static bool TryReadDouble(JsonArray array, int index, out double value)
    {
        value = 0;
        if (index < 0 || index >= array.Count || array[index] is not JsonValue jsonValue)
        {
            return false;
        }

        if (jsonValue.TryGetValue<double>(out value))
        {
            return true;
        }

        if (!jsonValue.TryGetValue<int>(out var integer))
        {
            return false;
        }

        value = integer;
        return true;
    }

    private static string InferRole(string? type)
    {
        var normalized = type?.ToLowerInvariant() ?? string.Empty;
        if (normalized.Contains("button", StringComparison.Ordinal)
            || normalized.Contains("gesture", StringComparison.Ordinal)
            || normalized.Contains("inkwell", StringComparison.Ordinal))
        {
            return "button";
        }

        if (normalized.Contains("textfield", StringComparison.Ordinal)
            || normalized.Contains("textinput", StringComparison.Ordinal)
            || normalized.Contains("edittext", StringComparison.Ordinal)
            || normalized.Contains("searchbar", StringComparison.Ordinal)
            || normalized.Contains("entry", StringComparison.Ordinal)
            || normalized.Contains("editor", StringComparison.Ordinal))
        {
            return "textbox";
        }

        if (normalized.Contains("scroll", StringComparison.Ordinal)
            || normalized.Contains("listview", StringComparison.Ordinal)
            || normalized.Contains("collectionview", StringComparison.Ordinal))
        {
            return "scrollview";
        }

        if (normalized.Contains("label", StringComparison.Ordinal)
            || normalized.Equals("text", StringComparison.Ordinal)
            || normalized.Contains("textview", StringComparison.Ordinal))
        {
            return "text";
        }

        return "view";
    }

    private static IReadOnlyList<string> InferActions(string role)
    {
        return role switch
        {
            "button" or "switch" or "checkbox" or "radio" => ["tap"],
            "textbox" => ["tap", "typeText", "focus"],
            "scrollview" => ["scroll", "swipe"],
            _ => []
        };
    }

    private static string? FirstString(params string?[] values)
        => values.FirstOrDefault(value => value is not null);

    private static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool IsActionReady(LiveUiNodeMatch match, LiveUiBounds viewport)
        => IsEffectivelyVisible(match)
           && IsEffectivelyEnabled(match)
           && HasUsableBoundsInViewport(match, viewport);
}

internal sealed record LiveUiRankedNodeMatch(LiveUiNodeMatch Match, int Index);

internal sealed record LiveUiNodeMatch(
    JsonObject Node,
    IReadOnlyList<JsonObject> Ancestors,
    VisualTreeTypeRegistry TypeRegistry);

internal sealed record LiveUiBounds(
    double X,
    double Y,
    double Width,
    double Height)
{
    public double CenterX => X + (Width / 2);

    public double CenterY => Y + (Height / 2);

    public JsonObject ToJson()
        => new()
        {
            ["x"] = X,
            ["y"] = Y,
            ["width"] = Width,
            ["height"] = Height
        };

    public static LiveUiBounds? Union(IReadOnlyList<LiveUiBounds> bounds)
    {
        if (bounds.Count == 0)
        {
            return null;
        }

        var left = bounds.Min(candidate => candidate.X);
        var top = bounds.Min(candidate => candidate.Y);
        var right = bounds.Max(candidate => candidate.X + candidate.Width);
        var bottom = bounds.Max(candidate => candidate.Y + candidate.Height);
        return new LiveUiBounds(left, top, right - left, bottom - top);
    }

    public static LiveUiBounds? Intersection(LiveUiBounds left, LiveUiBounds right)
    {
        var intersectionLeft = Math.Max(left.X, right.X);
        var intersectionTop = Math.Max(left.Y, right.Y);
        var intersectionRight = Math.Min(left.X + left.Width, right.X + right.Width);
        var intersectionBottom = Math.Min(left.Y + left.Height, right.Y + right.Height);
        return intersectionRight > intersectionLeft && intersectionBottom > intersectionTop
            ? new LiveUiBounds(
                intersectionLeft,
                intersectionTop,
                intersectionRight - intersectionLeft,
                intersectionBottom - intersectionTop)
            : null;
    }
}
