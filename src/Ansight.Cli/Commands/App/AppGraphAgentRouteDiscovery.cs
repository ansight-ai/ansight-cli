using System.Text;
using System.Text.Json;
using Ansight.Host;

namespace Ansight.Cli.Commands.App;

internal static class AppGraphAgentRouteDiscovery
{
    private const int MaximumCandidateGraphs = 3;
    private const int MaximumRouteTransitions = 16;
    private const int MaximumBindingsPerTransition = 3;
    private static readonly HashSet<string> stopWords = new(StringComparer.Ordinal)
    {
        "a", "an", "and", "app", "at", "be", "by", "can", "do", "for", "from", "in", "into",
        "is", "it", "of", "on", "open", "or", "screen", "select", "tap", "the", "then", "to",
        "use", "wait", "with"
    };
    private static readonly JsonSerializerOptions jsonOptions = new(JsonSerializerDefaults.Web);

    internal static async Task<AppGraphAgentRouteDiscoveryResult> DiscoverAsync(
        IAppGraphAgentRouteCatalog catalog,
        Guid? teamId,
        string appId,
        IReadOnlyList<string> instructions,
        bool isEnabled,
        bool requirePublished,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        ArgumentNullException.ThrowIfNull(instructions);
        if (!isEnabled)
        {
            return AppGraphAgentRouteDiscoveryResult.Empty;
        }
        if (!teamId.HasValue || teamId.Value == Guid.Empty)
        {
            return AppGraphAgentRouteDiscoveryResult.Unavailable(
                "App Graph route guidance was requested, but no authorized signed-in organisation was selected.");
        }

        var appsResult = await catalog.ListRegisteredAppsAsync(teamId.Value, cancellationToken)
            .ConfigureAwait(false);
        if (!appsResult.IsSuccess)
        {
            return AppGraphAgentRouteDiscoveryResult.Unavailable(appsResult.Message);
        }

        var registeredApp = appsResult.Apps.FirstOrDefault(app =>
            app.TeamId == teamId.Value
            && string.Equals(app.AppId, appId, StringComparison.OrdinalIgnoreCase));
        if (registeredApp is null)
        {
            return AppGraphAgentRouteDiscoveryResult.Unavailable(
                $"No registered organisation app matched '{appId}', so no App Graph data was included.");
        }

        var graphsResult = await catalog.ListAppGraphsAsync(teamId.Value, cancellationToken)
            .ConfigureAwait(false);
        if (!graphsResult.IsSuccess)
        {
            return AppGraphAgentRouteDiscoveryResult.Unavailable(graphsResult.Message);
        }

        var requestedIntent = string.Join(' ', instructions.Where(static instruction =>
            !string.IsNullOrWhiteSpace(instruction)));
        var availableGraphs = graphsResult.Graphs
            .Where(graph => graph.TeamId == teamId.Value
                            && graph.TeamAppId == registeredApp.Id
                            && (requirePublished
                                ? graph.PublishedVersionId.HasValue
                                : graph.CurrentVersionId.HasValue || graph.PublishedVersionId.HasValue))
            .Select(graph => new RankedAppGraph(graph, Score(requestedIntent, $"{graph.Name} {graph.Intent}")))
            .OrderByDescending(static candidate => candidate.Score)
            .ThenByDescending(static candidate => candidate.Graph.UpdatedAt)
            .ToArray();
        var candidates = availableGraphs
            .Where(candidate => candidate.Score > 0 || availableGraphs.Length == 1)
            .Take(MaximumCandidateGraphs)
            .ToArray();
        if (candidates.Length == 0)
        {
            return AppGraphAgentRouteDiscoveryResult.Unavailable(
                $"No authorized published App Graph matched the requested intent for '{appId}'.");
        }

        var plans = new List<SimulatorAgentAppGraphPlan>();
        var warnings = new List<string>();
        foreach (var candidate in candidates)
        {
            var detailResult = await catalog.GetPublishedAppGraphAsync(
                    candidate.Graph.Id,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!detailResult.IsSuccess || detailResult.Detail is null)
            {
                warnings.Add(detailResult.Message);
                continue;
            }

            var detail = detailResult.Detail;
            var expectedVersionId = requirePublished
                ? detail.Graph.PublishedVersionId
                : detail.Graph.CurrentVersionId ?? detail.Graph.PublishedVersionId;
            if (detail.Graph.TeamId != teamId.Value
                || detail.Graph.TeamAppId != registeredApp.Id
                || expectedVersionId != detail.Version.Id)
            {
                warnings.Add($"App Graph '{detail.Graph.Name}' did not match the authorized app scope.");
                continue;
            }

            var requestedTarget = ResolveBestTargetLabel(detail, requestedIntent);
            var plan = AppGraphPlanner.Build(detail, requestedTarget);
            if (plan.Errors.Count > 0)
            {
                warnings.Add($"Published App Graph '{detail.Graph.Name}' is not executable: {string.Join(" ", plan.Errors)}");
                continue;
            }
            if (plan.Steps.Count == 0 || plan.Steps.Count > MaximumRouteTransitions)
            {
                warnings.Add(
                    $"Published App Graph '{detail.Graph.Name}' produced {plan.Steps.Count} transitions; "
                    + $"agent route guidance requires between 1 and {MaximumRouteTransitions}.");
                continue;
            }

            plans.Add(new SimulatorAgentAppGraphPlan(
                detail.Graph.Id,
                detail.Version.Id,
                detail.Graph.Name,
                detail.Graph.Intent,
                plan.Steps[^1].To.Name,
                plan.Steps.Select((step, index) => new SimulatorAgentAppGraphTransition(
                    index + 1,
                    step.Edge.Id,
                    step.From.Name,
                    step.To.Name,
                    step.Edge.Action?.SemanticMeaning ?? string.Empty,
                    step.Edge.Postconditions,
                    step.Bindings
                        .Take(MaximumBindingsPerTransition)
                        .Select(binding => new SimulatorAgentAppGraphBinding(
                            binding.Id,
                            binding.Priority,
                            binding.Mechanism,
                            binding.Configuration.DeepClone().AsObject(),
                            binding.Preconditions,
                            binding.Postconditions,
                            binding.Confidence))
                        .ToArray()))
                    .ToArray()));
        }

        if (plans.Count == 0 && warnings.Count == 0)
        {
            warnings.Add($"No executable published App Graph route was available for '{appId}'.");
        }

        return new AppGraphAgentRouteDiscoveryResult(plans, warnings);
    }

    private static string? ResolveBestTargetLabel(CloudAppGraphDetail detail, string requestedIntent)
    {
        try
        {
            var definition = detail.Version.Definition.Deserialize<AppGraphDocument>(jsonOptions);
            if (definition is null)
            {
                return null;
            }

            var targets = definition.Nodes.Where(node =>
                node.Kind.Equals("outcome", StringComparison.OrdinalIgnoreCase)
                || node.Kind.Equals("assertion", StringComparison.OrdinalIgnoreCase)
                || !definition.Edges.Any(edge => string.Equals(edge.From, node.Id, StringComparison.Ordinal)))
                .Select(node => new
                {
                    Node = node,
                    Score = Score(
                        requestedIntent,
                        $"{node.Name} {string.Join(' ', node.Synonyms ?? [])} {node.Purpose}")
                })
                .OrderByDescending(static candidate => candidate.Score)
                .ThenBy(static candidate => candidate.Node.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return targets.FirstOrDefault()?.Score > 0 ? targets[0].Node.Name : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static int Score(string requestedIntent, string candidateText)
    {
        var requestedTokens = Tokenize(requestedIntent);
        if (requestedTokens.Count == 0)
        {
            return 0;
        }

        var candidateTokens = Tokenize(candidateText);
        var score = requestedTokens.Count(candidateTokens.Contains);
        var normalizedCandidate = candidateText.Trim();
        if (normalizedCandidate.Length >= 6
            && requestedIntent.Contains(normalizedCandidate, StringComparison.OrdinalIgnoreCase))
        {
            score += 8;
        }

        return score;
    }

    private static HashSet<string> Tokenize(string value)
    {
        var normalized = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            normalized.Append(char.IsLetterOrDigit(character)
                ? char.ToLowerInvariant(character)
                : ' ');
        }

        return normalized.ToString()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(token => token.Length >= 3 && !stopWords.Contains(token))
            .ToHashSet(StringComparer.Ordinal);
    }

    private sealed record RankedAppGraph(CloudAppGraphSummary Graph, int Score);
}
