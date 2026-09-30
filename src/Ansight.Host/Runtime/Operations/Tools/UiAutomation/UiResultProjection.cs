using System.Text.Json.Nodes;

namespace Ansight.Host.Runtime.Operations.Tools.UiAutomation;

/// <summary>Projects UI result nodes while retaining the tool's verdicts, counts and audit evidence.</summary>
internal static class UiResultProjection
{
    public static JsonObject Project(JsonObject content, UiProjectionOptions options)
    {
        var result = content.DeepClone().AsObject();
        var budget = new ProjectionBudget();
        ProjectEnvelope(result, options, budget);
        if (budget.SourceNodes > 0)
        {
            result["projectionSourceNodeCount"] = budget.SourceNodes;
            result["projectionReturnedNodeCount"] = budget.ReturnedNodes;
            if (budget.Truncated) result["projectionTruncated"] = true;
            if (budget.Characters > Math.Max(0, options.MaximumCharacters)
                || budget.ReturnedNodes > Math.Max(0, options.MaximumNodes))
            {
                // Exact verdict targets and supplied selectors are never shortened to fit a budget.
                result["projectionBudgetExceeded"] = true;
            }
        }
        UiNodeProjection.CompactBoundsInPlace(result);
        return result;
    }

    private static void ProjectEnvelope(JsonObject envelope, UiProjectionOptions options, ProjectionBudget budget)
    {
        var groups = new List<NodeGroup>();
        foreach (var name in new[] { "selected", "target" })
        {
            if (envelope[name] is JsonObject node)
            {
                groups.Add(new NodeGroup(name, false, 1, [UiNodeProjection.FromResult(node, options)]));
            }
        }
        var availableNodes = Math.Max(0, options.MaximumNodes - budget.ReturnedNodes - groups.Count);
        foreach (var name in new[] { "matches", "diagnosticMatches" })
        {
            if (envelope[name] is not JsonArray nodes) continue;
            var limit = Math.Min(availableNodes, Math.Max(0, options.MaximumMatches));
            var projected = nodes.OfType<JsonObject>().Take(limit)
                .Select(node => UiNodeProjection.FromResult(node, options)).ToList();
            groups.Add(new NodeGroup(name, true, nodes.Count, projected));
            availableNodes -= projected.Count;
        }

        if (groups.Count > 0)
        {
            var projection = BuildProjection(groups, envelope["ancestors"] as JsonArray, options);
            var maximumCharacters = Math.Max(0, options.MaximumCharacters - budget.Characters);
            while (projection.ToJsonString().Length > maximumCharacters)
            {
                // Drop diagnostic candidates first. Preserve selected/action targets and their verdicts.
                var removable = groups.LastOrDefault(group => group.IsArray && group.Nodes.Count > 0);
                if (removable is null) break;
                removable.Nodes.RemoveAt(removable.Nodes.Count - 1);
                projection = BuildProjection(groups, envelope["ancestors"] as JsonArray, options);
            }
            foreach (var field in projection) envelope[field.Key] = field.Value?.DeepClone();
            var sourceCount = groups.Sum(group => group.SourceCount);
            var returnedCount = groups.Sum(group => group.Nodes.Count);
            var truncated = returnedCount < sourceCount
                            || ContainsTruncation(projection);
            budget.SourceNodes += sourceCount;
            budget.ReturnedNodes += returnedCount;
            budget.Characters += projection.ToJsonString().Length;
            budget.Truncated |= truncated;
            if (truncated) envelope["projectionTruncated"] = true;
            envelope["projectionSourceNodeCount"] = sourceCount;
            envelope["projectionReturnedNodeCount"] = returnedCount;
        }

        // Restrict recursion to result envelopes. Selectors, evidence and already-projected trees
        // are data, and must not acquire different meaning because they contain a familiar key.
        foreach (var name in new[] { "result", "payload", "actions", "steps", "results" })
        {
            if (envelope[name] is JsonObject nested)
            {
                ProjectEnvelope(nested, options, budget);
            }
            else if (envelope[name] is JsonArray children)
            {
                foreach (var child in children.OfType<JsonObject>()) ProjectEnvelope(child, options, budget);
            }
        }
    }

    private static JsonObject BuildProjection(IReadOnlyList<NodeGroup> groups, JsonArray? existingAncestors,
        UiProjectionOptions options)
    {
        var result = new JsonObject();
        var projectedNodes = new List<JsonObject>();
        foreach (var group in groups)
        {
            if (group.IsArray)
            {
                var nodes = group.Nodes.Select(node => node.DeepClone().AsObject()).ToArray();
                result[group.Name] = new JsonArray(nodes);
                projectedNodes.AddRange(nodes);
            }
            else
            {
                var node = group.Nodes[0].DeepClone().AsObject();
                result[group.Name] = node;
                projectedNodes.Add(node);
            }
        }
        var ancestors = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var ancestor in existingAncestors?.OfType<JsonObject>() ?? [])
        {
            if (ReadIdentity(ancestor) is { } id) ancestors.TryAdd(id, UiNodeProjection.Ancestor(ancestor, null, options));
        }
        foreach (var node in projectedNodes)
        {
            if (!options.ShareAncestors || node["ancestorPath"] is not JsonArray path || path.Count == 0
                || path.OfType<JsonObject>().Any(ancestor => ReadIdentity(ancestor) is null))
            {
                // Matching labels or automation IDs alone cannot establish shared source identity.
                continue;
            }
            string? parentId = null;
            foreach (var ancestor in path.OfType<JsonObject>())
            {
                var id = ReadIdentity(ancestor)!;
                if (!ancestors.TryGetValue(id, out var shared))
                {
                    shared = ancestor.DeepClone().AsObject();
                    ancestors.Add(id, shared);
                }
                if (parentId is not null && parentId != id) shared["parentId"] = parentId;
                parentId = id;
            }
            if (parentId is not null && parentId != ReadIdentity(node)) node["parentId"] = parentId;
            node.Remove("ancestorPath");
        }
        var reachable = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in projectedNodes)
        {
            var parentId = ReadText(node, "parentId");
            while (parentId is not null && ancestors.TryGetValue(parentId, out var ancestor) && reachable.Add(parentId))
            {
                parentId = ReadText(ancestor, "parentId");
            }
        }
        if (reachable.Count > 0 || existingAncestors is not null)
        {
            result["ancestors"] = new JsonArray(ancestors.Where(entry => reachable.Contains(entry.Key))
                .Select(entry => (JsonNode?)entry.Value).ToArray());
        }
        return result;
    }

    private static bool ContainsTruncation(JsonNode node)
        => node switch
        {
            JsonObject value => value["projectionTruncated"] is JsonValue flag && flag.TryGetValue<bool>(out var truncated) && truncated
                                || value.Any(property => property.Value is { } child && ContainsTruncation(child)),
            JsonArray values => values.OfType<JsonNode>().Any(ContainsTruncation),
            _ => false
        };

    private static string? ReadText(JsonObject node, string name)
        => node[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static string? ReadIdentity(JsonObject node)
    {
        foreach (var name in new[] { "id", "nodeId" })
        {
            if (node[name] is JsonValue value && value.TryGetValue<string>(out var id) && !string.IsNullOrWhiteSpace(id)) return id;
        }
        return null;
    }

    private sealed record NodeGroup(string Name, bool IsArray, int SourceCount, List<JsonObject> Nodes);

    private sealed class ProjectionBudget
    {
        public int SourceNodes { get; set; }
        public int ReturnedNodes { get; set; }
        public int Characters { get; set; }
        public bool Truncated { get; set; }
    }
}
