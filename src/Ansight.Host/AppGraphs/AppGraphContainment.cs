namespace Ansight.Host.AppGraphs;

/// <summary>Validates direct ownership between observed destination instances, independently of UI framework.</summary>
public static class AppGraphContainment
{
    public static IReadOnlyList<string> FindErrors(IEnumerable<AppGraphContainmentRelation> relations)
    {
        var errors = new List<string>();
        var parents = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var relation in relations)
        {
            if (relation.ParentDestinationId == relation.ChildDestinationId)
            {
                errors.Add($"Destination '{relation.ChildDestinationId}' cannot own itself.");
                continue;
            }
            if (parents.TryGetValue(relation.ChildDestinationId, out var parent)
                && parent != relation.ParentDestinationId)
            {
                errors.Add($"Destination '{relation.ChildDestinationId}' has multiple direct owners: '{parent}' and '{relation.ParentDestinationId}'. Preserve nested ownership instead of flattening descendants.");
                continue;
            }

            parents[relation.ChildDestinationId] = relation.ParentDestinationId;
        }

        foreach (var child in parents.Keys)
        {
            var visited = new HashSet<string>(StringComparer.Ordinal) { child };
            var current = child;
            while (parents.TryGetValue(current, out var parent))
            {
                if (!visited.Add(parent))
                {
                    errors.Add($"Navigation ownership contains a cycle involving '{parent}'.");
                    break;
                }

                current = parent;
            }
        }

        return errors.Distinct(StringComparer.Ordinal).ToArray();
    }
}
