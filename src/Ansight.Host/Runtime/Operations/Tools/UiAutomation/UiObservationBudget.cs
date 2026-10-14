using System.Text.Json.Nodes;

namespace Ansight.Host.Runtime.Operations.Tools.UiAutomation;

/// <summary>Budgets the completed hierarchy, including shared ancestry and after-action evidence.</summary>
internal static class UiObservationBudget
{
    public static void Apply(JsonObject envelope, UiProjectionOptions options)
    {
        var observations = FindObservations(envelope).ToArray();
        if (observations.Length == 0) return;
        while (envelope.ToJsonString().Length > options.MaximumCharacters
               || observations.Sum(CountNodes) > options.MaximumNodes)
        {
            // Structure metadata repeats the hierarchy. Discard it before actionable controls.
            var structure = observations.Select(item => item["structureNodes"]).OfType<JsonArray>()
                .FirstOrDefault(items => items.Count > 0);
            if (structure is not null)
            {
                var owner = structure.Parent!.AsObject();
                structure.RemoveAt(structure.Count - 1);
                owner["truncated"] = true;
                continue;
            }
            var removable = observations.SelectMany(observation => Leaves(observation["root"] as JsonObject))
                .Reverse()
                .OrderBy(node => IsNavigation(node) ? 1 : 0)
                .ThenBy(node => node["onScreen"]?.GetValue<bool>() == true ? 1 : 0)
                .ThenBy(node => node["supportedActions"] is JsonArray { Count: > 0 } ? 1 : 0)
                .FirstOrDefault();
            if (removable?.Parent is not JsonArray siblings) break;
            var observation = observations.First(item => IsDescendant(removable, item));
            siblings.Remove(removable);
            observation["truncated"] = true;
            observation["returnedNodeCount"] = CountReturned(observation["root"] as JsonObject, true);
        }
        if (envelope.ToJsonString().Length > options.MaximumCharacters)
            envelope["projectionBudgetExceeded"] = true;
        foreach (var observation in observations)
        {
            if (observation["structureNodes"] is not JsonArray structure) continue;
            var identities = Nodes(observation["root"] as JsonObject)
                .Concat(structure.OfType<JsonObject>()).Select(node => node["id"]?.GetValue<string>())
                .OfType<string>().ToHashSet(StringComparer.Ordinal);
            foreach (var node in structure.OfType<JsonObject>())
                if (node["parentId"] is JsonValue id && id.TryGetValue<string>(out var parent) && !identities.Contains(parent))
                    node.Remove("parentId");
        }
    }

    private static IEnumerable<JsonObject> FindObservations(JsonObject envelope)
    {
        if (envelope["capability"]?.GetValue<string>() == "ui.observe" && envelope["root"] is JsonObject)
            yield return envelope;
        foreach (var key in new[] { "result", "payload", "afterObservation", "actions", "steps", "results" })
        {
            if (envelope[key] is JsonObject nested)
                foreach (var item in FindObservations(nested)) yield return item;
            else if (envelope[key] is JsonArray values)
                foreach (var child in values.OfType<JsonObject>())
                    foreach (var item in FindObservations(child)) yield return item;
        }
    }

    private static IEnumerable<JsonObject> Nodes(JsonObject? root)
    {
        if (root is null) yield break;
        yield return root;
        if (root["children"] is JsonArray children)
            foreach (var child in children.OfType<JsonObject>())
                foreach (var node in Nodes(child)) yield return node;
    }

    private static IEnumerable<JsonObject> Leaves(JsonObject? root)
        => Nodes(root).Where(node => !ReferenceEquals(node, root)
                                    && (node["children"] is not JsonArray children || children.Count == 0));
    // MaximumNodes counts returned descendants; the required root is part of the character budget.
    private static int CountNodes(JsonObject observation) => Math.Max(0, Nodes(observation["root"] as JsonObject).Count() - 1)
        + ((observation["structureNodes"] as JsonArray)?.Count ?? 0);
    private static int CountReturned(JsonObject? root, bool excludeRoot)
        => Nodes(root).Count(node => (!excludeRoot || !ReferenceEquals(node, root))
                                    && node["contextOnly"]?.GetValue<bool>() != true);
    private static bool IsDescendant(JsonNode node, JsonNode ancestor)
    {
        for (var parent = node.Parent; parent is not null; parent = parent.Parent)
            if (ReferenceEquals(parent, ancestor)) return true;
        return false;
    }
    private static bool IsNavigation(JsonObject node)
        => node["role"]?.GetValue<string>() is "tab" or "tablist" or "dialog" or "alertdialog" or "navigation"
           || node["currentPage"]?.GetValue<bool>() == true || node["activePage"]?.GetValue<bool>() == true
           || node["type"]?.GetValue<string>()?.Contains("Page", StringComparison.OrdinalIgnoreCase) == true;
}
