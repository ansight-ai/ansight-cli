using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ansight.Cli.Commands.App.Learning;

/// <summary>Ranks observed navigation routes by potential reuse, not user frequency.</summary>
internal static class AppLearnPlanner
{
    public static AppLearnPlan Build(JsonObject definition, JsonObject evidence,
        IReadOnlySet<string> existingTaskIds, int maximumTasks)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumTasks, 1);
        if (definition["schema"]?.GetValue<string>() != "ansight.app-graph/v1"
            || definition["nodes"] is not JsonArray nodes || nodes.Count == 0
            || definition["edges"] is not JsonArray edges)
            throw new InvalidDataException("Learning requires an observed App Graph with destinations and transitions.");

        var diagnostics = new List<string>();
        var destinations = nodes.OfType<JsonObject>().ToDictionary(node => Text(node, "id"), StringComparer.Ordinal);
        // The exploration compiler preserves first-observed order; this is the observed starting state.
        var entry = nodes[0]!.AsObject();
        var entryId = Text(entry, "id");
        var bindings = (evidence["bindingCandidates"] as JsonArray ?? [])
            .OfType<JsonObject>().ToArray();
        var usableEdges = new List<JsonObject>();
        foreach (var edge in edges.OfType<JsonObject>())
        {
            var id = Text(edge, "id");
            var automationId = (edge["action"] as JsonObject)?["automationId"]?.GetValue<string>();
            var binding = bindings.FirstOrDefault(candidate => Text(candidate, "edgeId") == id
                && Text(candidate, "mechanism") == "ui_action"
                && candidate["configuration"] is JsonObject configuration
                && Text(configuration, "action") == "tap"
                && configuration["selector"]?["automationId"]?.GetValue<string>() == automationId);
            if (binding is null || string.IsNullOrWhiteSpace(automationId)
                || (edge["parameters"] as JsonArray)?.Count > 0
                || !destinations.ContainsKey(Text(edge, "from")) || !destinations.ContainsKey(Text(edge, "to")))
            {
                diagnostics.Add($"Skipped transition '{id}': requires an observed fixed-input UI tap binding.");
                continue;
            }
            usableEdges.Add(edge);
        }

        var paths = new Dictionary<string, IReadOnlyList<JsonObject>>(StringComparer.Ordinal) { [entryId] = [] };
        var queue = new Queue<string>();
        queue.Enqueue(entryId);
        while (queue.TryDequeue(out var current))
        {
            if (paths[current].Count >= 12) continue;
            foreach (var edge in usableEdges.Where(edge => Text(edge, "from") == current)
                         .OrderBy(edge => Text(edge, "id"), StringComparer.Ordinal))
            {
                var target = Text(edge, "to");
                if (paths.ContainsKey(target)) continue;
                paths[target] = [.. paths[current], edge];
                queue.Enqueue(target);
            }
        }

        var candidates = new List<AppLearnCandidate>();
        var seenRoutes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var destination in destinations.Values)
        {
            var targetId = Text(destination, "id");
            if (!paths.TryGetValue(targetId, out var path) || path.Count == 0) continue;
            // Persist an identity based on observed action IDs, independent of generated graph IDs and ordering.
            var signature = JsonSerializer.Serialize(new
            {
                start = Text(entry, "name"),
                target = Text(destination, "name"),
                actions = path.Select(edge => edge["action"]!["automationId"]!.GetValue<string>()).ToArray()
            });
            if (!seenRoutes.Add(signature)) continue;
            var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(signature)))[..10];
            var label = Slug(Text(destination, "name"));
            var taskName = $"Learn {label} {hash}";
            var taskId = $"learn-{label}-{hash}";
            if (existingTaskIds.Contains(taskId))
            {
                diagnostics.Add($"Preserved existing task '{taskId}'.");
                continue;
            }
            var sharedRoutes = paths.Values.Count(other => other.Count > path.Count
                && path.Select(edge => Text(edge, "id")).SequenceEqual(
                    other.Take(path.Count).Select(edge => Text(edge, "id"))));
            var score = path.Count * 2 + sharedRoutes * 3;
            var route = new JsonObject
            {
                ["startingDestination"] = entry.DeepClone(),
                ["targetDestination"] = destination.DeepClone(),
                ["transitions"] = new JsonArray(path.Select(edge => edge.DeepClone()).ToArray())
            };
            var candidate = new AppLearnCandidate(taskName, taskId, Text(entry, "name"), Text(destination, "name"),
                path.Count, score, $"{path.Count} navigation step(s); shared setup for {sharedRoutes} other destination(s).", route);
            if (AppLearnPrompts.Author(candidate).Length > 8_000)
                diagnostics.Add($"Skipped destination '{targetId}': its evidence exceeds the task author's handoff limit.");
            else
                candidates.Add(candidate);
        }

        if (candidates.Count == 0)
            diagnostics.Add("No new reusable navigation routes were found from the observed starting destination.");
        return new(candidates.OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.TaskId, StringComparer.Ordinal).Take(maximumTasks).ToArray(), diagnostics);
    }

    private static string Text(JsonObject value, string property) => value[property]?.GetValue<string>() ?? string.Empty;

    private static string Slug(string value)
    {
        var slug = string.Join('-', value.ToLowerInvariant().Split(
            value.Where(character => !char.IsAsciiLetterOrDigit(character)).Distinct().ToArray(),
            StringSplitOptions.RemoveEmptyEntries));
        return string.IsNullOrEmpty(slug) ? "destination" : slug[..Math.Min(slug.Length, 40)].TrimEnd('-');
    }
}
