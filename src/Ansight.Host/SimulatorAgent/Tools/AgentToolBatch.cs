using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ansight.Host.SimulatorAgent.Tools;

/// <summary>Expands a model's ordered plan through the ordinary guarded tool-call loop.</summary>
internal sealed class AgentToolBatch(
    IReadOnlyList<OpenAiFunctionCall> calls,
    JsonArray availableTools,
    Action<string, string> publishOutput,
    Action<AgentBatchCompletion> onCompleted)
{
    public const string UiToolName = "ansight_run_ui_batch";
    private const int MaximumSteps = 8;
    private static readonly string[] allowedTools =
    ["ansight_find_ui", "ansight_tap_ui", "ansight_long_press_ui", "ansight_type_text", "ansight_wait_for_ui", "ansight_assert_ui"];
    private static readonly HashSet<string> actionOptions = new(StringComparer.Ordinal)
    { "durationMs", "value", "replaceExisting", "settleMs", "includeScreenshot" };
    private ToolCallResult? lastResult;
    private JsonArray? results;
    private OpenAiFunctionCall? activeCall;
    private IReadOnlyList<AgentBatchStep> activeSteps = [];
    private DateTimeOffset started;
    private Stopwatch? stopwatch;

    public string? BatchCallId { get; private set; }
    public int? StepIndex { get; private set; }
    public int? StepCount { get; private set; }
    public string? ValidationError { get; private set; }
    public int CooldownBeforeStepMilliseconds { get; private set; }

    public static JsonObject? BuildUiDefinition(JsonArray tools)
    {
        var names = tools.OfType<JsonObject>().Select(tool => ReadString(tool["name"]))
            .Where(name => name is not null && allowedTools.Contains(name)).ToArray();
        if (names.Length == 0) return null;
        return new JsonObject
        {
            ["type"] = "function", ["name"] = UiToolName, ["strict"] = false,
            ["description"] = "Run 1–8 UI tools in order without another model pass. Each step uses the named tool's argument schema and normal guards. Stops on failure or an ambiguous/empty find. Set usePreviousTarget=true on tap/long-press/type immediately after find to copy its unique exact target. Use only when the whole short plan is known; return to the model for choices between results. Every step is traced separately.",
            ["parameters"] = new JsonObject
            {
                ["type"] = "object", ["additionalProperties"] = false,
                ["required"] = new JsonArray("steps"),
                ["properties"] = new JsonObject
                {
                    ["steps"] = new JsonObject
                    {
                        ["type"] = "array", ["minItems"] = 1, ["maxItems"] = MaximumSteps,
                        ["items"] = BuildStepSchema(names!, tools)
                    }
                }
            }
        };
    }

    private static JsonObject BuildStepSchema(string?[] names, JsonArray tools)
    {
        var variants = new JsonArray(new JsonObject
        {
            ["type"] = "object", ["additionalProperties"] = false,
            ["required"] = new JsonArray("toolName", "arguments"),
            ["properties"] = new JsonObject
            {
                ["toolName"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray(names.Select(name => (JsonNode?)JsonValue.Create(name)).ToArray()) },
                ["arguments"] = new JsonObject { ["type"] = "object", ["description"] = "Arguments using this tool's loaded schema and an observed target." },
                ["usePreviousTarget"] = new JsonObject { ["type"] = "boolean", ["enum"] = new JsonArray(false) }
            }
        });
        foreach (var name in names.Where(name => name is "ansight_tap_ui" or "ansight_long_press_ui" or "ansight_type_text"))
        {
            var definition = tools.OfType<JsonObject>().First(tool => ReadString(tool["name"]) == name);
            var properties = new JsonObject();
            foreach (var option in actionOptions)
                if (definition["parameters"]?["properties"]?[option] is { } schema)
                    properties[option] = schema.DeepClone();
            var required = new JsonArray();
            if (name == "ansight_type_text")
            {
                properties["value"] ??= new JsonObject { ["type"] = "string" };
                required.Add("value");
            }
            variants.Add(new JsonObject
            {
                ["type"] = "object", ["additionalProperties"] = false,
                ["required"] = new JsonArray("toolName", "arguments", "usePreviousTarget"),
                ["properties"] = new JsonObject
                {
                    ["toolName"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray(name) },
                    ["usePreviousTarget"] = new JsonObject { ["type"] = "boolean", ["enum"] = new JsonArray(true) },
                    ["arguments"] = new JsonObject
                    {
                        ["type"] = "object", ["additionalProperties"] = false,
                        ["description"] = "Action options only. The host copies the preceding find's complete target; do not supply selectors, action, index or coordinates.",
                        ["properties"] = properties, ["required"] = required
                    }
                }
            });
        }
        return new JsonObject { ["anyOf"] = variants };
    }

    public IEnumerable<OpenAiFunctionCall> ReadCalls()
    {
        foreach (var call in calls)
        {
            ValidationError = null;
            if (call.Name is not (UiToolName or AgentTaskBatch.ToolName))
            {
                yield return call;
                continue;
            }
            var valid = call.Name == AgentTaskBatch.ToolName
                ? AgentTaskBatch.TryParse(call.Arguments, availableTools, out var steps, out var error)
                : TryParseUi(call.Arguments, out steps, out error);
            if (!valid)
            {
                ValidationError = error;
                yield return call;
                continue;
            }

            activeCall = call;
            activeSteps = steps;
            started = DateTimeOffset.UtcNow;
            stopwatch = Stopwatch.StartNew();
            BatchCallId = call.CallId;
            StepCount = steps.Count;
            results = new JsonArray();
            lastResult = null;
            string? stoppedReason = null;
            var failed = false;
            try
            {
                for (var index = 0; index < steps.Count; index++)
                {
                    var step = steps[index];
                    StepIndex = index + 1;
                    CooldownBeforeStepMilliseconds = index > 0 ? steps[index - 1].CooldownMilliseconds : 0;
                    var arguments = step.Arguments.DeepClone().AsObject();
                    if (step.UsePreviousTarget)
                    {
                        if (!TryReadUniqueTarget(lastResult?.Output, out var selector))
                            ValidationError = "The previous find did not return one unambiguous exact target. Inspect its results before continuing.";
                        else
                            foreach (var property in selector!) arguments[property.Key] = property.Value?.DeepClone();
                    }
                    lastResult = null;
                    yield return new OpenAiFunctionCall($"{call.CallId}:step:{index + 1}", step.ToolName, arguments);
                    if (lastResult is null) throw new InvalidOperationException("A batch step completed without a recorded result.");
                    if (lastResult.IsError)
                    {
                        failed = true;
                        stoppedReason = lastResult.Message;
                        break;
                    }
                    if (step.ToolName == "ansight_find_ui" && !HasUniqueMatch(lastResult.Output))
                    {
                        stoppedReason = "The find result is missing, incomplete, or ambiguous. Inspect the results before choosing the next action.";
                        break;
                    }
                }
                CompleteBatch(stoppedReason, failed);
            }
            finally
            {
                // Disposal also occurs on cancellation, exceptions, and run limits.
                CompleteBatch(lastResult?.IsError == true ? lastResult.Message : "Batch interrupted before all steps completed.", true);
                activeCall = null;
                activeSteps = [];
                stopwatch = null;
                BatchCallId = null;
                StepIndex = null;
                StepCount = null;
                CooldownBeforeStepMilliseconds = 0;
                results = null;
                ValidationError = null;
            }
        }
    }

    public void RecordResult(OpenAiFunctionCall call, ToolCallResult result)
    {
        if (results is null)
        {
            publishOutput(call.CallId, result.ModelOutput ?? result.Output);
            return;
        }
        lastResult = result;
        results.Add(new JsonObject
        {
            ["step"] = StepIndex, ["toolName"] = call.Name, ["isError"] = result.IsError,
            ["taskId"] = call.Name == "ansight_run_task" ? call.Arguments["taskId"]?.DeepClone() : null,
            ["message"] = result.Message, ["result"] = ReadObject(result.ModelOutput ?? result.Output)
                ?? (JsonNode?)JsonValue.Create(result.ModelOutput ?? result.Output)
        });
    }

    public async Task WaitForCooldownAsync(CancellationToken cancellationToken)
    {
        if (CooldownBeforeStepMilliseconds == 0 || results?.LastOrDefault() is not JsonObject previousStep) return;
        var cooldown = new JsonObject { ["requestedMilliseconds"] = CooldownBeforeStepMilliseconds };
        previousStep["cooldown"] = cooldown;
        var timer = Stopwatch.StartNew();
        try
        {
            await Task.Delay(CooldownBeforeStepMilliseconds, cancellationToken).ConfigureAwait(false);
            cooldown["status"] = "completed";
        }
        catch (OperationCanceledException)
        {
            cooldown["status"] = "cancelled";
            throw;
        }
        finally
        {
            cooldown["elapsedMilliseconds"] = timer.ElapsedMilliseconds;
        }
    }

    public void Stop(string reason) => CompleteBatch(reason, true);

    private void CompleteBatch(string? stoppedReason, bool failed)
    {
        if (activeCall is not { } call || results is null) return;
        activeCall = null;
        var completed = results.Count;
        var message = stoppedReason is null
            ? $"Completed all {StepCount} batch steps."
            : $"Batch stopped after {completed} of {StepCount} steps: {stoppedReason}";
        if (call.Name == AgentTaskBatch.ToolName)
            message += $" Reason: {ReadString(call.Arguments["reason"])}";
        var payload = new JsonObject
        {
            ["isError"] = failed, ["completed"] = stoppedReason is null,
            ["requestedStepCount"] = StepCount, ["executedStepCount"] = completed,
            ["skippedStepCount"] = StepCount - completed,
            ["message"] = message, ["steps"] = results
        };
        if (call.Name == AgentTaskBatch.ToolName)
        {
            payload["reason"] = call.Arguments["reason"]?.DeepClone();
            payload["skippedSteps"] = new JsonArray(activeSteps.Skip(completed).Select((step, index) => (JsonNode?)new JsonObject
            {
                ["step"] = completed + index + 1, ["taskId"] = step.Arguments["taskId"]?.DeepClone(), ["status"] = "skipped"
            }).ToArray());
        }
        var output = payload.ToJsonString();
        publishOutput(call.CallId, output);
        onCompleted(new AgentBatchCompletion(call, started, stopwatch!.ElapsedMilliseconds,
            new ToolCallResult(failed, output, message)));
    }

    private bool TryParseUi(JsonObject arguments, out List<AgentBatchStep> steps, out string error)
    {
        steps = [];
        error = "A UI batch must contain 1–8 steps using available UI tools. Nested batches, tasks, and completion calls are not allowed.";
        var available = availableTools.OfType<JsonObject>().Select(tool => ReadString(tool["name"])).ToHashSet();
        if (!available.Contains(UiToolName) || arguments["steps"] is not JsonArray { Count: >= 1 and <= MaximumSteps } items)
            return false;
        foreach (var item in items)
        {
            if (item is not JsonObject step || ReadString(step["toolName"]) is not { } name
                || !allowedTools.Contains(name) || !available.Contains(name) || step["arguments"] is not JsonObject input)
                return false;
            var fromPrevious = step["usePreviousTarget"] is JsonValue flag && flag.TryGetValue<bool>(out var enabled) && enabled;
            if (step["usePreviousTarget"] is not null && (step["usePreviousTarget"] is not JsonValue scalar || !scalar.TryGetValue<bool>(out _)))
                return false;
            if (fromPrevious && (name is not ("ansight_tap_ui" or "ansight_long_press_ui" or "ansight_type_text")
                                 || steps.LastOrDefault()?.ToolName != "ansight_find_ui"
                                 || input.Any(property => property.Value is not null && !actionOptions.Contains(property.Key))))
            {
                error = "usePreviousTarget is only valid on tap/long-press/type immediately after find. Omit selector and coordinate overrides so the exact discovered target is preserved.";
                return false;
            }
            if (name == "ansight_type_text" && ReadString(input["value"]) is null)
            {
                error = "Every type step must include a string value.";
                return false;
            }
            steps.Add(new AgentBatchStep(name, input, fromPrevious));
        }
        return true;
    }

    private static bool HasUniqueMatch(string output)
    {
        var root = ReadObject(output);
        var result = root?["result"] as JsonObject ?? root;
        return result?["matches"] is JsonArray { Count: 1 } matches && matches[0] is JsonObject
               && result["totalMatches"] is JsonValue total && total.TryGetValue<int>(out var count) && count == 1
               && !(result["truncated"] is JsonValue truncated && truncated.TryGetValue<bool>(out var value) && value);
    }

    private static bool TryReadUniqueTarget(string? output, out JsonObject? selector)
    {
        selector = null;
        if (output is null || !HasUniqueMatch(output)) return false;
        var root = ReadObject(output);
        var result = root?["result"] as JsonObject ?? root;
        if (result?["matches"]?[0] is JsonObject match && match["tapHint"] is JsonObject hint)
            selector = hint["selector"] as JsonObject;
        return selector is { Count: > 0 };
    }

    private static JsonObject? ReadObject(string output)
    {
        try { return JsonNode.Parse(output) as JsonObject; }
        catch (JsonException) { return null; }
    }

    private static string? ReadString(JsonNode? value)
        => value is JsonValue scalar && scalar.TryGetValue<string>(out var text) ? text : null;
}
