using System.Text.Json.Nodes;

namespace Ansight.Host.AppGraphs;

/// <summary>Projects the existing graph JSON contract into framework-independent ownership relations.</summary>
public static class AppGraphContainmentReader
{
    public static IReadOnlyList<AppGraphContainmentRelation> ReadRelations(JsonObject graph)
    {
        var relations = new List<AppGraphContainmentRelation>();
        AddRelations(graph["navigationHosts"] as JsonArray, "destinationId", "childDestinationIds", relations);
        AddRelations(graph["tabGroups"] as JsonArray, "parentDestinationId", "tabDestinationIds", relations);

        return relations;
    }

    private static void AddRelations(
        JsonArray? containers,
        string parentProperty,
        string childrenProperty,
        ICollection<AppGraphContainmentRelation> relations)
    {
        if (containers is null)
        {
            return;
        }

        foreach (var container in containers.OfType<JsonObject>())
        {
            if (container[parentProperty] is not JsonValue parentValue
                || !parentValue.TryGetValue<string>(out var parent))
            {
                continue;
            }

            if (container[childrenProperty] is not JsonArray children)
            {
                continue;
            }

            foreach (var childValue in children.OfType<JsonValue>())
            {
                if (childValue.TryGetValue<string>(out var child))
                {
                    relations.Add(new AppGraphContainmentRelation(parent, child));
                }
            }
        }
    }
}
