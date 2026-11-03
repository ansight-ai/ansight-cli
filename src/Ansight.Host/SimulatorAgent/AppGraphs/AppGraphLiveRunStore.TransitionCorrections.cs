using System.Text.Json.Nodes;

namespace Ansight.Host.SimulatorAgent.AppGraphs;

internal sealed partial class AppGraphLiveRunStore
{
    private static string? ApplyTransitionCorrections(MutableRun run, JsonObject report, DateTimeOffset now)
    {
        if (report["transitionCorrections"] is not JsonArray corrections)
        {
            return null;
        }

        foreach (var value in corrections)
        {
            if (value is not JsonObject correction)
            {
                return "Each transition correction must identify a retained edge and observed evidence.";
            }

            var id = ReadString(correction, "id") ?? string.Empty;
            var from = ReadString(correction, "from") ?? string.Empty;
            var to = ReadString(correction, "to") ?? string.Empty;
            var evidence = NormalizeOptional(ReadString(correction, "evidence"));
            var valid = run.Edges.TryGetValue(id, out var edge)
                && run.Nodes.ContainsKey(from)
                && run.Nodes.ContainsKey(to)
                && evidence is not null;
            if (!valid)
            {
                return "A transition correction requires a retained edge ID, known source/destination IDs, and observed evidence of the attribution error.";
            }

            var duplicate = run.Edges.Values.FirstOrDefault(candidate =>
            {
                var sameCorrectedTransition = candidate.Id != id
                    && candidate.To == to
                    && HasSameTransitionAction(candidate, from, edge!.AutomationId, edge.Selector);
                return sameCorrectedTransition;
            });
            if (duplicate is not null)
            {
                run.Edges.Remove(id);
                continue;
            }

            run.Edges[id] = edge! with
            {
                From = from,
                To = to,
                SemanticMeaning = evidence!,
                NavigationContext = edge.NavigationContext is { } context
                    ? context with { ReturnDestinationId = to } : null,
                UpdatedUtc = now
            };
        }

        return null;
    }
}
