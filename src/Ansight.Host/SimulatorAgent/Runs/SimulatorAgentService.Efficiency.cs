using System.Text.Json.Nodes;
using Ansight.Host.Runtime.Tasks;

namespace Ansight.Host.SimulatorAgent;

public sealed partial class SimulatorAgentService
{
    private static IEnumerable<string> ReadAssessmentExclusions(ToolCallResult result)
    {
        if (result.IsError) return [];
        return (JsonNode.Parse(result.Output)?["excludedTaskIds"] as JsonArray)?
            .OfType<JsonValue>().Select(value => value.GetValue<string>()).ToArray() ?? [];
    }

    private static ToolCallResult CreateUncoveredStepAssessmentResult(
        JsonObject arguments,
        IReadOnlyList<RepositoryTaskShortcut> repositoryTasks,
        IReadOnlySet<string> attemptedTaskIds,
        IReadOnlySet<string> successfulTaskIds,
        IReadOnlySet<string> failedTaskIds)
    {
        if (arguments["taskDecisions"] is null)
            return CreateUncoveredStepDeclarationResult(arguments, repositoryTasks,
                attemptedTaskIds, successfulTaskIds, failedTaskIds);
        if (arguments["taskDecisions"] is not JsonArray decisions || decisions.Count > 5)
            return CreateAgentGuardResult("taskReassessmentDeclaration", "taskDecisions must contain at most five task exclusions.");

        // Validate the complete assessment before committing any exclusion to run state.
        var remaining = repositoryTasks.ToList();
        var exclusions = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in decisions)
        {
            if (item is not JsonObject decision || ReadString(decision, "taskId") is not { } taskId
                || ReadString(decision, "reason") is not ("scope-mismatch" or "missing-input")
                || ReadString(decision, "evidence") is null || !exclusions.Add(taskId))
                return CreateAgentGuardResult("taskReassessmentDeclaration", "Each task decision needs a unique taskId, scope-mismatch/missing-input reason and concrete evidence.");
            var single = arguments.DeepClone().AsObject();
            single.Remove("taskDecisions");
            single["relatedTaskId"] = taskId;
            single["reason"] = decision["reason"]!.DeepClone();
            single["evidence"] = decision["evidence"]!.DeepClone();
            single["consideredTaskIds"] = new JsonArray(remaining.Where(task => !attemptedTaskIds.Contains(task.TaskId))
                .Select(task => (JsonNode?)JsonValue.Create(task.TaskId)).ToArray());
            var validation = CreateUncoveredStepDeclarationResult(single, remaining,
                attemptedTaskIds, successfulTaskIds, failedTaskIds);
            if (validation.IsError) return validation;
            remaining.RemoveAll(task => task.TaskId == taskId);
        }

        var residual = arguments.DeepClone().AsObject();
        residual.Remove("taskDecisions");
        if (residual["consideredTaskIds"] is JsonArray considered)
            for (var index = considered.Count - 1; index >= 0; index--)
                if (considered[index] is JsonValue value && value.TryGetValue<string>(out var taskId)
                    && exclusions.Contains(taskId)) considered.RemoveAt(index);
        var result = CreateUncoveredStepDeclarationResult(residual, remaining,
            attemptedTaskIds, successfulTaskIds, failedTaskIds);
        if (result.IsError) return result;
        var output = JsonNode.Parse(result.Output)!.AsObject();
        output["excludedTaskIds"] = new JsonArray(exclusions.Order(StringComparer.Ordinal)
            .Select(id => (JsonNode?)JsonValue.Create(id)).ToArray());
        return result with { Output = output.ToJsonString() };
    }

    private static JsonObject BuildTaskDecisionsSchema() => new()
    {
        ["type"] = new JsonArray("array", "null"), ["maxItems"] = 5,
        ["description"] = "Optional exclusions of multiple unused tasks in one assessment. Each needs its own evidence. Describe the remaining manual step in the outer fields; use no-matching-task/null when no task remains.",
        ["items"] = new JsonObject
        {
            ["type"] = "object", ["additionalProperties"] = false,
            ["properties"] = new JsonObject
            {
                ["taskId"] = new JsonObject { ["type"] = "string" },
                ["reason"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("scope-mismatch", "missing-input") },
                ["evidence"] = new JsonObject { ["type"] = "string", ["minLength"] = 1 }
            },
            ["required"] = new JsonArray("taskId", "reason", "evidence")
        }
    };
}
