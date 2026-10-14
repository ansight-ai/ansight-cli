using System.Text.Json.Nodes;

namespace Ansight.Host.SimulatorAgent.Tools;

/// <summary>Defines a fixed task sequence; execution remains in the ordinary guarded tool loop.</summary>
internal static class AgentTaskBatch
{
    public const string ToolName = "ansight_run_task_batch";
    private const int MaximumSteps = 8;
    private const int MaximumReasonCharacters = 1000;
    private const int MaximumCooldownMilliseconds = 10_000;

    public static JsonObject? BuildDefinition(JsonArray tools)
    {
        if (!HasTool(tools, "ansight_run_task")) return null;
        return new JsonObject
        {
            ["type"] = "function", ["name"] = ToolName, ["strict"] = false,
            ["description"] = "Run 1–8 discovered repository tasks in order without another model pass. Supply exact task IDs and all inputs upfront. Explain why the sequence is appropriate, its prerequisites are established or established by earlier tasks, and no intermediate interpretation is needed. Each task retains normal guards, limits, assertions and traces. Stops on the first failure. Optional per-step cooldownMs waits after success only when another task follows; defaults to zero. Explain any cooldown in reason. No parallel execution, output references, UI steps or completion calls. Review results before completion; never replay a successful prefix.",
            ["parameters"] = new JsonObject
            {
                ["type"] = "object", ["additionalProperties"] = false,
                ["required"] = new JsonArray("reason", "steps"),
                ["properties"] = new JsonObject
                {
                    ["reason"] = new JsonObject
                    {
                        ["type"] = "string", ["minLength"] = 1, ["maxLength"] = MaximumReasonCharacters,
                        ["description"] = "Brief user-visible decision summary: why these tasks can be chained, what establishes their starting states, and why all inputs are known. Recorded in the batch result and execution trace."
                    },
                    ["steps"] = new JsonObject
                    {
                        ["type"] = "array", ["minItems"] = 1, ["maxItems"] = MaximumSteps,
                        ["items"] = new JsonObject
                        {
                            ["type"] = "object", ["additionalProperties"] = false,
                            ["required"] = new JsonArray("taskId", "input"),
                            ["properties"] = new JsonObject
                            {
                                ["taskId"] = new JsonObject { ["type"] = "string", ["minLength"] = 1, ["description"] = "Exact discovered repository ID, not a shortcut function name." },
                                ["input"] = new JsonObject { ["type"] = "object", ["description"] = "Inputs using this task's declared schema. Use {} when no inputs are needed. No references to earlier outputs." },
                                ["cooldownMs"] = new JsonObject
                                {
                                    ["type"] = "integer", ["minimum"] = 0, ["maximum"] = MaximumCooldownMilliseconds,
                                    ["description"] = "Optional UI settling time after this successful task and before the next task (0–10000 ms, default 0). Not applied after failure or the last task. Use only for a known settling need; a delay does not prove a postcondition."
                                }
                            }
                        }
                    }
                }
            }
        };
    }

    public static bool TryParse(JsonObject arguments, JsonArray availableTools, out List<AgentBatchStep> steps, out string error)
    {
        steps = [];
        error = "A task batch requires a non-empty reason (at most 1000 characters) and 1–8 steps containing an exact taskId, an input object and optional integer cooldownMs (0–10000). UI actions, nested batches, completion and target overrides are not allowed.";
        if (!HasTool(availableTools, ToolName) || !HasTool(availableTools, "ansight_run_task")
            || arguments.Any(property => property.Key is not ("reason" or "steps"))
            || arguments["reason"] is not JsonValue reason || !reason.TryGetValue<string>(out var explanation)
            || string.IsNullOrWhiteSpace(explanation) || explanation.Length > MaximumReasonCharacters
            || arguments["steps"] is not JsonArray { Count: >= 1 and <= MaximumSteps } items)
            return false;
        foreach (var item in items)
        {
            if (item is not JsonObject step || step.Any(property => property.Key is not ("taskId" or "input" or "cooldownMs"))
                || step["taskId"] is not JsonValue id || !id.TryGetValue<string>(out var taskId)
                || string.IsNullOrWhiteSpace(taskId) || step["input"] is not JsonObject input)
                return false;
            var cooldown = 0;
            if (step.ContainsKey("cooldownMs") && (step["cooldownMs"] is not JsonValue delay
                || !delay.TryGetValue<int>(out cooldown) || cooldown is < 0 or > MaximumCooldownMilliseconds))
                return false;
            steps.Add(new AgentBatchStep("ansight_run_task", new JsonObject
            {
                ["taskId"] = taskId, ["input"] = input.DeepClone()
            }, CooldownMilliseconds: cooldown));
        }
        return true;
    }

    private static bool HasTool(JsonArray tools, string name)
        => tools.OfType<JsonObject>().Any(tool => tool["name"] is JsonValue value && value.TryGetValue<string>(out var toolName) && toolName == name);
}
