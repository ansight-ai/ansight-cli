using System.Text.Json;
using System.Text.Json.Nodes;
using Ansight.Host.Runtime.Operations.Tools.RepositoryTasks;

namespace Ansight.Host.Runtime.Tasks;

internal static class RepositoryTaskProtocol
{
    public const int DefaultMaximumDiscoveryResults = 10;
    public const int MaximumDiscoveryResults = 20;
    private const int MaximumDiagnosticTasks = 200;

    public static JsonObject BuildDiscoveryResult(
        RepositoryTaskLoadResult loadResult,
        string appId,
        string? sessionId,
        string? query,
        string? feature,
        int maximumResults,
        bool includeDiagnostics = false)
    {
        ArgumentNullException.ThrowIfNull(loadResult);
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);

        var evaluations = includeDiagnostics
            ? loadResult.Tasks.ToDictionary(task => task.TaskId, task => RepositoryTaskSearchMatcher.Evaluate(task, query), StringComparer.Ordinal)
            : null;
        var rankedMatches = loadResult.Tasks
            .Where(task => feature is null
                           || string.Equals(task.Feature, feature, StringComparison.OrdinalIgnoreCase)
                           || task.Feature?.Contains(feature, StringComparison.OrdinalIgnoreCase) == true)
            .Select(task => evaluations is null ? RepositoryTaskSearchMatcher.Match(task, query) : evaluations[task.TaskId])
            .OfType<RepositoryTaskSearchMatch>()
            .Where(match => match.ExclusionReason is null)
            .OrderByDescending(match => query is null ? 0 : match.Score)
            .ThenBy(match => match.Task.Title, StringComparer.OrdinalIgnoreCase)
            .ThenBy(match => match.Task.TaskId, StringComparer.Ordinal);
        var taskMatches = rankedMatches
            .Take(Math.Clamp(maximumResults, 1, MaximumDiscoveryResults))
            .ToArray();
        var result = new JsonObject
        {
            ["appId"] = appId.Trim(),
            ["sessionId"] = string.IsNullOrWhiteSpace(sessionId) ? null : sessionId.Trim(),
            ["query"] = query,
            ["feature"] = feature,
            ["matchCount"] = taskMatches.Length,
            ["tasks"] = SerializeTaskMatches(taskMatches),
            ["warnings"] = new JsonArray(loadResult.Warnings.Select(warning => (JsonNode?)warning).ToArray())
        };
        if (includeDiagnostics)
        {
            var returnedTaskIds = taskMatches.Select(match => match.Task.TaskId).ToHashSet(StringComparer.Ordinal);
            var diagnosticTasks = loadResult.Tasks.Take(MaximumDiagnosticTasks).ToArray();
            result["diagnostics"] = new JsonObject
            {
                ["availableTaskCount"] = loadResult.Tasks.Count,
                ["availableTaskIds"] = new JsonArray(diagnosticTasks.Select(task => (JsonNode?)JsonValue.Create(task.TaskId)).ToArray()),
                ["truncated"] = loadResult.Tasks.Count > diagnosticTasks.Length,
                ["candidates"] = new JsonArray(diagnosticTasks.Select(task =>
                {
                    var match = evaluations![task.TaskId];
                    var featureMatches = feature is null
                        || string.Equals(task.Feature, feature, StringComparison.OrdinalIgnoreCase)
                        || task.Feature?.Contains(feature, StringComparison.OrdinalIgnoreCase) == true;
                    return (JsonNode?)new JsonObject
                    {
                        ["taskId"] = task.TaskId,
                        ["score"] = match.Score,
                        ["coverage"] = match.Coverage,
                        ["reason"] = !featureMatches ? "feature-filter"
                            : match.ExclusionReason is { } exclusionReason ? exclusionReason
                            : returnedTaskIds.Contains(task.TaskId) ? "returned"
                            : "discovery-result-limit",
                        ["matchedQueryTerms"] = new JsonArray(match.MatchedQueryTerms.Select(term => (JsonNode?)JsonValue.Create(term)).ToArray()),
                        ["unmatchedQueryTerms"] = new JsonArray(match.UnmatchedQueryTerms.Select(term => (JsonNode?)JsonValue.Create(term)).ToArray())
                    };
                }).ToArray())
            };
        }
        return result;
    }

    public static JsonObject BuildRunResult(RepositoryTaskRunResult result, bool includeCallTrace = true)
    {
        ArgumentNullException.ThrowIfNull(result);
        var payload = new JsonObject
        {
            ["runId"] = result.RunId,
            ["appId"] = result.AppId,
            ["sessionId"] = result.SessionId,
            ["taskId"] = result.TaskId,
            ["status"] = result.Status.ToString(),
            ["startedAtUtc"] = result.StartedAtUtc,
            ["completedAtUtc"] = result.CompletedAtUtc,
            ["durationMilliseconds"] = result.DurationMilliseconds,
            ["message"] = result.Message,
            ["output"] = result.Output?.DeepClone(),
            ["assertions"] = includeCallTrace
                ? JsonSerializer.SerializeToNode(result.Assertions.Select(RepositoryTaskCallTrace.CaptureAssertion), JsonUtil.Compact)
                : new JsonArray(result.Assertions.Select(assertion => (JsonNode?)new JsonObject
            {
                ["assertionId"] = assertion.AssertionId,
                ["passed"] = assertion.Passed,
                ["message"] = assertion.Message
            }).ToArray()),
            ["toolCalls"] = JsonSerializer.SerializeToNode(
                includeCallTrace
                    ? result.ToolCalls
                    : result.ToolCalls.Select(toolCall => toolCall with
                    {
                        Arguments = null,
                        Result = null,
                        ChildCalls = null,
                        Assertions = null,
                        SourceTrace = null
                    }).ToArray(),
                JsonUtil.Compact)
        };
        if (includeCallTrace && result.SourceTrace is not null)
        {
            payload["sourceTrace"] = JsonSerializer.SerializeToNode(result.SourceTrace, JsonUtil.Compact);
        }
        if (!string.IsNullOrWhiteSpace(result.StandardError))
        {
            payload["standardError"] = result.StandardError.Length <= 2_000
                ? result.StandardError
                : $"{result.StandardError[..1_999]}…";
        }

        return payload;
    }

    private static JsonArray SerializeTaskMatches(IEnumerable<RepositoryTaskSearchMatch> matches)
        => new(matches.Select(match => (JsonNode?)SerializeTaskMatch(match)).ToArray());

    private static JsonObject SerializeTaskMatch(RepositoryTaskSearchMatch match)
    {
        var task = JsonSerializer.SerializeToNode(match.Task.ToPublicDefinition(), JsonUtil.Compact)!.AsObject();
        task["behavioralSynonyms"] = new JsonArray(
            match.BehavioralSynonyms.Select(synonym => (JsonNode?)synonym).ToArray());
        if (match.MatchedQueryTerms.Count == 0 && match.UnmatchedQueryTerms.Count == 0)
        {
            return task;
        }

        task["match"] = new JsonObject
        {
            ["score"] = match.Score,
            ["coverage"] = match.Coverage,
            ["matchedQueryTerms"] = new JsonArray(
                match.MatchedQueryTerms.Select(term => (JsonNode?)term).ToArray()),
            ["unmatchedQueryTerms"] = new JsonArray(
                match.UnmatchedQueryTerms.Select(term => (JsonNode?)term).ToArray()),
            ["synonymMatches"] = new JsonArray(match.Evidence
                .Where(evidence => evidence.Kind == RepositoryTaskSearchMatchKind.Synonym)
                .Select(evidence => (JsonNode?)$"{evidence.QueryTerm} -> {evidence.IndexedTerm}")
                .ToArray()),
            ["fuzzyMatches"] = new JsonArray(match.Evidence
                .Where(evidence => evidence.Kind == RepositoryTaskSearchMatchKind.Fuzzy)
                .Select(evidence => (JsonNode?)$"{evidence.QueryTerm} -> {evidence.IndexedTerm} ({evidence.EditDistance} edits)")
                .ToArray())
        };
        return task;
    }
}
