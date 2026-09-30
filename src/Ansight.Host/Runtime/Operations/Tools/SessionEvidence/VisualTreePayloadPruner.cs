using System.Text.Json.Nodes;

namespace Ansight.Host.Runtime.Operations.Tools.SessionEvidence;

internal static class VisualTreePayloadPruner
{
    public static JsonObject Prune(JsonObject payload, int? maxDepth)
    {
        var clone = payload.DeepClone() as JsonObject ?? new JsonObject();
        if (!maxDepth.HasValue || maxDepth.Value < 0)
        {
            return clone;
        }

        if (clone["root"] is JsonObject root)
        {
            PruneNode(root, depth: 0, maxDepth.Value);
        }

        return clone;
    }

    private static void PruneNode(JsonObject node, int depth, int maxDepth)
    {
        if (depth >= maxDepth)
        {
            if (node["children"] is JsonArray children)
            {
                node["truncatedChildren"] = children.Count;
            }

            node.Remove("children");
            return;
        }

        if (node["children"] is not JsonArray childArray)
        {
            return;
        }

        foreach (var child in childArray.OfType<JsonObject>())
        {
            PruneNode(child, depth + 1, maxDepth);
        }
    }
}
