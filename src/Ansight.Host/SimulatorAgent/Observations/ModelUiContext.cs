using System.Globalization;
using System.Text.Json.Nodes;

namespace Ansight.Host.SimulatorAgent.Observations;

// Run-local aliases belong to one observation. The counter never rewinds, so an
// expired selector cannot silently resolve to a different node after navigation.
internal sealed class ModelUiContext
{
    private readonly Dictionary<string, string> aliasesById = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> idsByAlias = new(StringComparer.Ordinal);
    private long nextAlias;

    public void Invalidate()
    {
        aliasesById.Clear();
        idsByAlias.Clear();
    }

    public JsonObject Project(JsonObject observation)
    {
        var result = observation.DeepClone().AsObject();
        UiNodeProjection.CompactBoundsInPlace(result);
        Compact(result);
        return result;
    }

    public bool TryResolveSelectors(JsonNode arguments, out string? error)
    {
        error = null;
        if (arguments is JsonObject value)
        {
            if (ReadString(value["nodeId"]) is { } nodeId && IsAlias(nodeId))
            {
                if (!idsByAlias.TryGetValue(nodeId, out var runtimeId))
                {
                    error = $"Node ID '{nodeId}' is unknown or expired. Use a fresh observation or an exact automation ID.";
                    return false;
                }
                value["nodeId"] = runtimeId;
            }
            foreach (var child in value.Select(entry => entry.Value).OfType<JsonNode>())
            {
                if (!TryResolveSelectors(child, out error)) return false;
            }
        }
        else if (arguments is JsonArray array)
        {
            foreach (var child in array.OfType<JsonNode>())
            {
                if (!TryResolveSelectors(child, out error)) return false;
            }
        }
        return true;
    }

    private void Compact(JsonNode node)
    {
        if (node is JsonArray array)
        {
            foreach (var child in array.OfType<JsonNode>()) Compact(child);
            return;
        }
        if (node is not JsonObject value) return;

        foreach (var entry in value.ToArray())
        {
            if (entry.Key is "id" or "nodeId" or "parentId" && ReadString(entry.Value) is { } id
                && (Guid.TryParse(id, out _) || IsAlias(id)))
            {
                if (!aliasesById.TryGetValue(id, out var alias))
                {
                    alias = "n" + (++nextAlias).ToString(CultureInfo.InvariantCulture);
                    aliasesById[id] = alias;
                    idsByAlias[alias] = id;
                }
                value[entry.Key] = alias;
            }
            else if (entry.Value is { } child)
            {
                Compact(child);
            }
        }
    }

    private static bool IsAlias(string value)
        => value.Length > 1 && value[0] == 'n' && value.AsSpan(1).IndexOfAnyExceptInRange('0', '9') < 0;

    private static string? ReadString(JsonNode? node)
        => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}
