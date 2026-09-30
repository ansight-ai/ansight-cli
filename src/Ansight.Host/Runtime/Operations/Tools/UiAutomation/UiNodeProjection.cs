using System.Text.Json.Nodes;

namespace Ansight.Host.Runtime.Operations.Tools.UiAutomation;

/// <summary>One node vocabulary for human and model observations; never used for runtime matching.</summary>
internal static class UiNodeProjection
{
    private static readonly string[] identityProperties =
        ["id", "nodeId", "parentId", "automationId", "type", "role", "source", "viewportRelation"];
    private static readonly string[] stateProperties =
        ["visible", "enabled", "onScreen", "selected", "active", "currentPage", "activePage", "focused", "checked",
            "expanded", "editable", "scrollable", "isPassword", "password", "isOpen", "contextOnly"];
    private static readonly string[] matchProperties = ["confidence", "matchMode", "matchScore", "matchReason"];

    public static JsonObject FromMatch(LiveUiNodeMatch match, UiProjectionOptions options, bool includeAncestors = true)
    {
        var result = FromResult(match.Node, options, includeAncestors);
        Set(result, "automationId", LiveUiNodeQuery.ReadAutomationId(match.Node));
        SetDisplayText(result, "text", LiveUiNodeQuery.ReadText(match.Node), options);
        // FromResult keeps the published scalar value. Do not promote values from framework
        // internals (visual/properties/props), which can include secure input implementation state.
        Set(result, "type", match.TypeRegistry.Resolve(match.Node));
        Set(result, "role", LiveUiNodeQuery.ReadRole(match.Node, match.TypeRegistry));
        result["visible"] = LiveUiNodeQuery.IsEffectivelyVisible(match);
        result["enabled"] = LiveUiNodeQuery.IsEffectivelyEnabled(match);
        var pageType = ReadText(result, "type")?.EndsWith("Page", StringComparison.OrdinalIgnoreCase) == true
                       || string.Equals(ReadText(result, "role"), "page", StringComparison.OrdinalIgnoreCase);
        foreach (var name in new[] { "currentPage", "activePage" })
        {
            if (!result.ContainsKey(name) && match.TypeRegistry.ReadState(match.Node, name) is { } state
                && (state || pageType))
            {
                result[name] = state;
            }
        }
        var actions = LiveUiNodeQuery.ReadSupportedActions(match.Node, match.TypeRegistry);
        if (actions.Count > 0)
        {
            result["supportedActions"] = new JsonArray(actions.Select(action => (JsonNode?)JsonValue.Create(action)).ToArray());
        }
        if (LiveUiNodeQuery.ReadBounds(match.Node) is { } bounds)
        {
            result["bounds"] = CompactBounds(bounds.ToJson());
        }
        if (includeAncestors) SetAncestors(result, match.Ancestors, match.TypeRegistry, options);
        return result;
    }

    public static JsonObject FromResult(JsonObject source, UiProjectionOptions options, bool includeAncestors = true)
    {
        var result = new JsonObject();
        foreach (var name in identityProperties) CopyScalar(source, result, name);
        foreach (var name in stateProperties) CopyScalar(source, result, name);
        foreach (var name in matchProperties) CopyScalar(source, result, name);
        foreach (var name in new[] { "text", "value", "matchedText" })
        {
            if (source[name] is JsonValue scalar && scalar.TryGetValue<string>(out var text))
            {
                SetDisplayText(result, name, text, options);
            }
            else
            {
                CopyScalar(source, result, name);
            }
            if (ReadBoolean(source, name + "Truncated")) result[name + "Truncated"] = true;
        }
        if (source["supportedActions"] is JsonArray actions)
        {
            result["supportedActions"] = new JsonArray(actions.OfType<JsonValue>()
                .Where(action => action.TryGetValue<string>(out _))
                .Select(action => action.DeepClone()).ToArray());
        }
        if (source["bounds"] is { } bounds) result["bounds"] = CompactBounds(bounds);

        // A null hint is an authoritative refusal. A missing hint does not establish uniqueness.
        if (source.TryGetPropertyValue("tapHint", out var hint)) result["tapHint"] = hint?.DeepClone();
        if (includeAncestors && source["ancestorPath"] is JsonArray ancestors)
        {
            SetAncestors(result, ancestors.OfType<JsonObject>(), null, options);
        }
        if (includeAncestors && ReadBoolean(source, "ancestorPathTruncated")) result["ancestorPathTruncated"] = true;
        if (ReadBoolean(source, "projectionTruncated")) result["projectionTruncated"] = true;
        return result;
    }

    public static JsonObject Ancestor(JsonObject source, VisualTreeTypeRegistry? registry, UiProjectionOptions options)
    {
        var result = new JsonObject();
        foreach (var name in new[] { "id", "nodeId", "parentId" }) CopyScalar(source, result, name);
        Set(result, "automationId", LiveUiNodeQuery.ReadAutomationId(source));
        SetDisplayText(result, "text", LiveUiNodeQuery.ReadText(source), options);
        Set(result, "type", registry is null ? ReadText(source, "type") : registry.Resolve(source));
        Set(result, "role", registry is null ? ReadText(source, "role") : LiveUiNodeQuery.ReadRole(source, registry));
        if (ReadBoolean(source, "textTruncated")) result["textTruncated"] = true;
        return result;
    }

    public static JsonNode CompactBounds(JsonNode bounds)
        => bounds is JsonObject value && value.Count == 4
           && value["x"] is JsonValue && value["y"] is JsonValue
           && value["width"] is JsonValue && value["height"] is JsonValue
            ? new JsonArray(value["x"]!.DeepClone(), value["y"]!.DeepClone(),
                value["width"]!.DeepClone(), value["height"]!.DeepClone())
            : bounds.DeepClone();

    public static void CompactBoundsInPlace(JsonNode node)
    {
        if (node is JsonArray array)
        {
            foreach (var child in array.OfType<JsonNode>()) CompactBoundsInPlace(child);
        }
        else if (node is JsonObject value)
        {
            foreach (var property in value.ToArray())
            {
                if (property.Value is null) continue;
                if (property.Key == "bounds") value[property.Key] = CompactBounds(property.Value);
                else CompactBoundsInPlace(property.Value);
            }
        }
    }

    private static void SetAncestors(JsonObject result, IEnumerable<JsonObject> ancestors,
        VisualTreeTypeRegistry? registry, UiProjectionOptions options)
    {
        var projected = ancestors.Select(ancestor => Ancestor(ancestor, registry, options))
            .Where(ancestor => ancestor.Count > 0).ToArray();
        var meaningful = projected.Where(ancestor => !string.IsNullOrWhiteSpace(ReadText(ancestor, "automationId"))
                                                     || !string.IsNullOrWhiteSpace(ReadText(ancestor, "text"))).ToArray();
        // Stable labelled context must not be displaced by a chain of anonymous layout wrappers.
        // With no meaningful context, retain bounded source identities rather than guessing a label.
        var useful = meaningful.Length > 0 ? meaningful : projected;
        var retained = useful.TakeLast(Math.Max(0, options.MaximumAncestors)).ToArray();
        result.Remove("ancestorPath");
        if (retained.Length > 0) result["ancestorPath"] = new JsonArray(retained);
        if (retained.Length < projected.Length)
        {
            result["ancestorPathTruncated"] = true;
            result["projectionTruncated"] = true;
        }
        if (retained.Any(ancestor => ReadBoolean(ancestor, "projectionTruncated"))) result["projectionTruncated"] = true;
    }

    private static void SetDisplayText(JsonObject result, string name, string? text, UiProjectionOptions options)
    {
        if (text is null) return;
        var maximum = Math.Max(0, options.MaximumTextCharacters);
        if (text.Length <= maximum)
        {
            result[name] = text;
            return;
        }
        result[name] = maximum > 0 ? text[..Math.Max(0, maximum - 1)] + "…" : string.Empty;
        result[name + "Truncated"] = true;
        result["projectionTruncated"] = true;
    }

    private static void CopyScalar(JsonObject source, JsonObject result, string name)
    {
        if (source[name] is JsonValue value) result[name] = value.DeepClone();
    }

    private static void Set(JsonObject result, string name, string? value)
    {
        if (value is not null) result[name] = value;
    }

    private static string? ReadText(JsonObject value, string name)
        => value[name] is JsonValue scalar && scalar.TryGetValue<string>(out var text) ? text : null;

    private static bool ReadBoolean(JsonObject value, string name)
        => value[name] is JsonValue scalar && scalar.TryGetValue<bool>(out var flag) && flag;
}
