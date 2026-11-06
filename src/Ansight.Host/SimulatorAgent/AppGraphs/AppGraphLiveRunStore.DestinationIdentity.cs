using System.Text.Json.Nodes;

namespace Ansight.Host.SimulatorAgent.AppGraphs;

internal sealed partial class AppGraphLiveRunStore
{
    private static bool IsDestinationWithin(
        IReadOnlyDictionary<string, SimulatorAgentAppGraphLiveNode> nodes, string destinationId, string ancestorId)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);

        while (visited.Add(destinationId) && nodes.TryGetValue(destinationId, out var node))
        {
            if (destinationId == ancestorId)
            {
                return true;
            }

            if (string.IsNullOrEmpty(node.ParentScreen))
            {
                return false;
            }

            destinationId = node.ParentScreen;
        }

        return false;
    }

    private static string? ValidateNewDestinationIdentity(JsonObject destination, MutableRun run)
    {
        var id = ReadString(destination, "id");

        if (id is null || run.Nodes.ContainsKey(id))
        {
            return null;
        }

        var name = NormalizeOptional(ReadString(destination, "name"));
        var kind = ReadString(destination, "kind");
        var matches = run.Nodes.Values.Where(node => node.Kind == kind
            && string.Equals(NormalizeIdentity(node.Name), NormalizeIdentity(name), StringComparison.Ordinal)).ToArray();

        if (matches.Length == 0 || NormalizeOptional(ReadString(destination, "identityEvidence")) is not null)
        {
            return null;
        }

        return $"Destination '{name}' already exists as {string.Join(", ", matches.Select(node => node.Id))}. "
            + "Reuse the observed canonical state. A genuinely distinct same-named state requires identityEvidence describing its route, container, or visible state difference.";
    }

    private static string? ApplyDestinationAliases(MutableRun run, JsonObject report)
    {
        if (report["destinationAliases"] is not JsonArray aliases)
        {
            return null;
        }

        foreach (var value in aliases)
        {
            if (value is not JsonObject alias)
            {
                return "Each destination alias must be an object.";
            }

            var sourceId = ReadString(alias, "from");
            var targetId = ReadString(alias, "to");
            var invalid = sourceId is null
                || targetId is null
                || sourceId == targetId
                || !run.Nodes.ContainsKey(sourceId)
                || !run.Nodes.ContainsKey(targetId)
                || NormalizeOptional(ReadString(alias, "evidence")) is null;

            if (invalid)
            {
                return "A destination alias needs two distinct retained IDs and observed evidence that they represent the same visual state.";
            }

            var source = run.Nodes[sourceId!];
            var target = run.Nodes[targetId!];

            if (source.Kind != target.Kind || target.ParentScreen == sourceId)
            {
                return "Destination aliases cannot merge different kinds or a parent into its child.";
            }

            string Redirect(string id) => id == sourceId ? targetId! : id;

            run.Nodes.Remove(sourceId!);

            foreach (var node in run.Nodes.Values.ToArray())
            {
                if (node.ParentScreen == sourceId)
                {
                    run.Nodes[node.Id] = node with
                    {
                        ParentScreen = targetId
                    };
                }
            }

            foreach (var edge in run.Edges.Values.ToArray())
            {
                run.Edges[edge.Id] = edge with
                {
                    From = Redirect(edge.From),
                    To = Redirect(edge.To),
                    NavigationContext = edge.NavigationContext is { } context
                        ? context with
                        {
                            ReturnDestinationId = Redirect(context.ReturnDestinationId)
                        } : null
                };
            }

            foreach (var host in run.NavigationHosts.Values.ToArray())
            {
                run.NavigationHosts[host.Id] = host with
                {
                    DestinationId = Redirect(host.DestinationId),
                    ActiveChildDestinationId = Redirect(host.ActiveChildDestinationId),
                    ChildDestinationIds = host.ChildDestinationIds.Select(Redirect).Distinct().ToArray()
                };
            }

            foreach (var group in run.TabGroups.Values.ToArray())
            {
                run.TabGroups[group.Id] = group with
                {
                    ParentDestinationId = Redirect(group.ParentDestinationId),
                    SelectedDestinationId = Redirect(group.SelectedDestinationId),
                    TabDestinationIds = group.TabDestinationIds.Select(Redirect).Distinct().ToArray()
                };
            }

            foreach (var action in run.Actions.Values.ToArray())
            {
                run.Actions[action.Id] = action with
                {
                    DestinationId = Redirect(action.DestinationId),
                    ResultDestinationId = action.ResultDestinationId is { } result ? Redirect(result) : null
                };
            }

            if (run.CurrentDestinationId == sourceId)
            {
                run.CurrentDestinationId = targetId;
            }

            if (run.PendingOutcome is { } pending)
            {
                run.PendingOutcome = pending with
                {
                    FromDestinationId = Redirect(pending.FromDestinationId)
                };
            }
        }

        return null;
    }
}
