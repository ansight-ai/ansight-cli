namespace Ansight.Host.Runtime.WebSocketSessions;

using System.Text.Json.Nodes;
using Ansight.Host.Runtime.Operations;

internal sealed class AppToolCatalogCache
{
    private readonly Lock gate = new();
    private readonly Dictionary<string, CachedToolDefinition> definitionsById = new(StringComparer.Ordinal);
    private JsonObject? indexCatalog;
    private string? revision;
    private string? availabilityRevision;
    private long updateVersion;
    private DateTimeOffset? initialCaptureReuseExpiresUtc;

    public void Clear()
    {
        lock (gate)
        {
            definitionsById.Clear();
            indexCatalog = null;
            revision = null;
            availabilityRevision = null;
            updateVersion++;
            initialCaptureReuseExpiresUtc = null;
        }
    }

    public AppToolCatalogCacheSnapshot GetSnapshot()
    {
        lock (gate)
        {
            return new AppToolCatalogCacheSnapshot(
                revision,
                availabilityRevision,
                indexCatalog?.DeepClone().AsObject(),
                updateVersion);
        }
    }

    public void Update(JsonObject payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        lock (gate)
        {
            updateVersion++;
            revision = ReadString(payload["revision"]) ?? revision;
            availabilityRevision = ReadString(payload["availabilityRevision"]) ?? availabilityRevision;

            if (ReadBoolean(payload["unchanged"], fallback: false))
            {
                ApplyAvailabilityChanges(payload);
                return;
            }

            if (payload["tools"] is not JsonArray tools)
            {
                return;
            }

            var detail = ReadString(payload["detail"]);
            if (string.Equals(detail, "definitions", StringComparison.Ordinal))
            {
                StoreDefinitions(tools);
                return;
            }

            var isCompactIndex = string.Equals(detail, "index", StringComparison.Ordinal);
            var indexTools = new JsonArray();
            foreach (var tool in tools.OfType<JsonObject>())
            {
                // Catalogs from pre-v3 SDKs do not declare a detail level and may
                // legitimately expose tools with no argument or result schema. Treat
                // those entries as complete definitions so the host does not issue a
                // second request that the legacy app cannot understand.
                if (!isCompactIndex)
                {
                    StoreDefinition(tool);
                }

                indexTools.Add(CreateIndexEntry(tool));
            }

            var replacement = payload.DeepClone().AsObject();
            replacement["detail"] = "index";
            replacement["tools"] = indexTools;
            indexCatalog = replacement;
        }
    }

    public void MarkInitialCaptureReusable()
    {
        lock (gate)
        {
            if (indexCatalog?["tools"] is JsonArray { Count: > 0 })
            {
                initialCaptureReuseExpiresUtc = DateTimeOffset.UtcNow.AddSeconds(5);
            }
        }
    }

    public bool TryTakeInitialCaptureReuse()
    {
        lock (gate)
        {
            var canReuse = initialCaptureReuseExpiresUtc is { } expiresUtc
                           && expiresUtc >= DateTimeOffset.UtcNow
                           && indexCatalog is not null;
            initialCaptureReuseExpiresUtc = null;
            return canReuse;
        }
    }

    public IReadOnlyList<string> GetSelectedToolIds(JsonObject? arguments)
    {
        lock (gate)
        {
            if (indexCatalog is null)
            {
                return Array.Empty<string>();
            }

            var filtered = ApplyFilter(indexCatalog, arguments);
            return filtered?["tools"] is JsonArray tools
                ? tools
                    .OfType<JsonObject>()
                    .Select(tool => ReadString(tool["id"]))
                    .Where(static toolId => toolId is not null)
                    .Select(static toolId => toolId!)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray()
                : Array.Empty<string>();
        }
    }

    public IReadOnlyList<string> GetMissingDefinitionIds(IReadOnlyList<string> toolIds)
    {
        ArgumentNullException.ThrowIfNull(toolIds);

        lock (gate)
        {
            var indexRevisions = ReadIndexDefinitionRevisions();
            return toolIds
                .Where(toolId => !definitionsById.TryGetValue(toolId, out var definition)
                                 || indexRevisions.TryGetValue(toolId, out var expectedRevision)
                                 && !string.Equals(definition.Revision, expectedRevision, StringComparison.Ordinal))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
        }
    }

    public JsonObject? BuildCatalog(JsonObject? arguments)
    {
        lock (gate)
        {
            if (indexCatalog is null)
            {
                return null;
            }

            var filtered = ApplyFilter(indexCatalog, arguments);
            if (filtered?["tools"] is not JsonArray tools)
            {
                return filtered;
            }
            if (string.Equals(
                    ReadString(arguments?["detail"]),
                    "summary",
                    StringComparison.OrdinalIgnoreCase))
            {
                return filtered;
            }

            var mergedTools = new JsonArray();
            foreach (var indexTool in tools.OfType<JsonObject>())
            {
                var toolId = ReadString(indexTool["id"]);
                if (toolId is null || !definitionsById.TryGetValue(toolId, out var cachedDefinition))
                {
                    mergedTools.Add(indexTool.DeepClone());
                    continue;
                }

                var merged = cachedDefinition.Definition.DeepClone().AsObject();
                CopyIfPresent(indexTool, merged, "runtime");
                CopyIfPresent(indexTool, merged, "executable");
                CopyIfPresent(indexTool, merged, "denial");
                CopyIfPresent(indexTool, merged, "definitionRevision");
                CopyIfPresent(indexTool, merged, "prerequisiteToolIds");
                mergedTools.Add(merged);
            }

            filtered["tools"] = mergedTools;
            return filtered;
        }
    }

    private void ApplyAvailabilityChanges(JsonObject payload)
    {
        if (indexCatalog?["tools"] is not JsonArray tools
            || payload["changes"] is not JsonObject changes)
        {
            return;
        }

        foreach (var tool in tools.OfType<JsonObject>())
        {
            var preserveAppNotExecutable = string.Equals(
                ReadString(tool["denial"]?["code"]),
                "tool_not_executable",
                StringComparison.Ordinal);
            tool.Remove("runtime");
            tool.Remove("denial");
            if (!preserveAppNotExecutable)
            {
                tool.Remove("executable");
            }

            var toolId = ReadString(tool["id"]);
            if (toolId is null || changes[toolId] is not JsonObject runtime)
            {
                continue;
            }

            tool["runtime"] = runtime.DeepClone();
            if (!ReadBoolean(runtime["available"], fallback: true)
                || !ReadBoolean(runtime["executable"], fallback: true))
            {
                tool["executable"] = false;
            }
        }

        indexCatalog["availabilityRevision"] = availabilityRevision;
        CopyIfPresent(payload, indexCatalog, "evaluatedAtUtc");
    }

    private void StoreDefinitions(JsonArray tools)
    {
        foreach (var tool in tools.OfType<JsonObject>())
        {
            StoreDefinition(tool);
        }
    }

    private void StoreDefinition(JsonObject tool)
    {
        var toolId = ReadString(tool["id"]);
        if (toolId is null)
        {
            return;
        }

        var definition = tool.DeepClone().AsObject();
        definition.Remove("runtime");
        definition.Remove("executable");
        definition.Remove("denial");
        definitionsById[toolId] = new CachedToolDefinition(
            definition,
            ReadString(tool["definitionRevision"]));
    }

    private Dictionary<string, string> ReadIndexDefinitionRevisions()
    {
        var revisions = new Dictionary<string, string>(StringComparer.Ordinal);
        if (indexCatalog?["tools"] is not JsonArray tools)
        {
            return revisions;
        }

        foreach (var tool in tools.OfType<JsonObject>())
        {
            var toolId = ReadString(tool["id"]);
            var definitionRevision = ReadString(tool["definitionRevision"]);
            if (toolId is not null && definitionRevision is not null)
            {
                revisions[toolId] = definitionRevision;
            }
        }

        return revisions;
    }

    private static JsonObject CreateIndexEntry(JsonObject tool)
    {
        var indexEntry = tool.DeepClone().AsObject();
        indexEntry.Remove("argumentsSchema");
        indexEntry.Remove("resultSchema");
        return indexEntry;
    }

    private static JsonObject ApplyFilter(JsonObject catalog, JsonObject? arguments)
        => arguments is null
            ? catalog.DeepClone().AsObject()
            : RemoteAppToolCatalogFilter.Apply(catalog, arguments)?.AsObject()
              ?? catalog.DeepClone().AsObject();

    private static void CopyIfPresent(JsonObject source, JsonObject destination, string propertyName)
    {
        if (source[propertyName] is { } value)
        {
            destination[propertyName] = value.DeepClone();
        }
        else
        {
            destination.Remove(propertyName);
        }
    }

    private static string? ReadString(JsonNode? value)
        => value is JsonValue jsonValue
           && jsonValue.TryGetValue<string>(out var text)
           && !string.IsNullOrWhiteSpace(text)
            ? text.Trim()
            : null;

    private static bool ReadBoolean(JsonNode? value, bool fallback)
        => value is JsonValue jsonValue && jsonValue.TryGetValue<bool>(out var result)
            ? result
            : fallback;

    private sealed record CachedToolDefinition(JsonObject Definition, string? Revision);
}

internal sealed record AppToolCatalogCacheSnapshot(
    string? Revision,
    string? AvailabilityRevision,
    JsonObject? IndexCatalog,
    long UpdateVersion);
