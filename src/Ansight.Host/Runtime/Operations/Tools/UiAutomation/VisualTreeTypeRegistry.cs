using System.Text.Json.Nodes;

namespace Ansight.Host.Runtime.Operations.Tools.UiAutomation;

internal sealed class VisualTreeTypeRegistry
{
    private readonly IReadOnlyList<string> types;
    private readonly int visibleFlag;
    private readonly int enabledFlag;
    private readonly int currentPageFlag;
    private readonly int activePageFlag;

    private VisualTreeTypeRegistry(
        IReadOnlyList<string> types,
        int visibleFlag,
        int enabledFlag,
        int currentPageFlag,
        int activePageFlag)
    {
        this.types = types;
        this.visibleFlag = visibleFlag;
        this.enabledFlag = enabledFlag;
        this.currentPageFlag = currentPageFlag;
        this.activePageFlag = activePageFlag;
    }

    public static VisualTreeTypeRegistry FromPayload(JsonObject payload)
    {
        var types = payload["types"] is JsonArray typeArray
            ? typeArray
                .Select(type => type is JsonValue value && value.TryGetValue<string>(out var name)
                    ? name
                    : string.Empty)
                .ToArray()
            : [];
        return new VisualTreeTypeRegistry(
            types,
            ReadFlag(payload, "visible", fallback: 1),
            ReadFlag(payload, "enabled", fallback: 2),
            ReadFlag(payload, "currentPage", fallback: 4),
            ReadFlag(payload, "activePage", fallback: 8));
    }

    public static bool TryCreateCompactV2(
        JsonObject payload,
        out VisualTreeTypeRegistry? typeRegistry,
        out JsonObject? root)
    {
        typeRegistry = null;
        root = payload["root"] as JsonObject;
        if (root is null
            || payload["types"] is not JsonArray typeArray
            || typeArray.Count == 0
            || payload["format"] is not JsonValue formatValue
            || !formatValue.TryGetValue<string>(out var format)
            || !format.EndsWith(".compact.v2", StringComparison.Ordinal))
        {
            return false;
        }

        var types = new List<string>(typeArray.Count);
        foreach (var type in typeArray)
        {
            if (type is not JsonValue value
                || !value.TryGetValue<string>(out var name)
                || string.IsNullOrWhiteSpace(name))
            {
                return false;
            }

            types.Add(name);
        }

        var pending = new Stack<JsonObject>();
        pending.Push(root);
        while (pending.TryPop(out var node))
        {
            if (node["typeId"] is not JsonValue typeIdValue
                || !typeIdValue.TryGetValue<int>(out var typeId)
                || typeId < 0
                || typeId >= types.Count
                || node.ContainsKey("type")
                || node.ContainsKey("kind")
                || node.ContainsKey("styleId")
                || node.ContainsKey("stacking"))
            {
                return false;
            }

            if (node["children"] is null)
            {
                continue;
            }

            if (node["children"] is not JsonArray children)
            {
                return false;
            }

            foreach (var child in children)
            {
                if (child is not JsonObject childNode)
                {
                    return false;
                }

                pending.Push(childNode);
            }
        }

        typeRegistry = new VisualTreeTypeRegistry(
            types,
            ReadFlag(payload, "visible", fallback: 1),
            ReadFlag(payload, "enabled", fallback: 2),
            ReadFlag(payload, "currentPage", fallback: 4),
            ReadFlag(payload, "activePage", fallback: 8));
        return true;
    }

    public bool? ReadState(JsonObject node, string propertyName)
    {
        if (node["flags"] is not JsonValue value
            || !value.TryGetValue<int>(out var flags))
        {
            return null;
        }

        var flag = propertyName switch
        {
            "visible" => visibleFlag,
            "enabled" => enabledFlag,
            "currentPage" => currentPageFlag,
            "activePage" => activePageFlag,
            _ => 0
        };
        return flag == 0 ? null : (flags & flag) == flag;
    }

    public string? Resolve(JsonObject node)
    {
        if (node["typeId"] is not JsonValue value
            || !value.TryGetValue<int>(out var typeId)
            || typeId < 0
            || typeId >= types.Count)
        {
            return null;
        }

        return string.IsNullOrWhiteSpace(types[typeId]) ? null : types[typeId];
    }

    private static int ReadFlag(JsonObject payload, string propertyName, int fallback)
    {
        return payload["flagBits"] is JsonObject flagBits
               && flagBits[propertyName] is JsonValue value
               && value.TryGetValue<int>(out var flag)
            ? flag
            : fallback;
    }
}
