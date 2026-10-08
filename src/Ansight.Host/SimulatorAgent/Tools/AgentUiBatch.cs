using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ansight.Host.SimulatorAgent.Tools;

/// <summary>Expands a model's ordered plan through the ordinary guarded tool-call loop.</summary>
internal sealed class AgentUiBatch(
    IReadOnlyList<OpenAiFunctionCall> calls,
    JsonArray availableTools,
    Action<string, string> publishOutput,
    Action<AgentUiBatchCompletion> onCompleted)
{
    public const string ToolName = "ansight_run_ui_batch";
    private const int MaximumSteps = 8;
    private static readonly string[] allowedTools =
    ["ansight_find_ui", "ansight_tap_ui", "ansight_type_text", "ansight_wait_for_ui", "ansight_assert_ui"];
    private static readonly HashSet<string> actionOptions = new(StringComparer.Ordinal)
    { "value", "replaceExisting", "settleMs", "includeScreenshot" };
    private ToolCallResult? lastResult;
    private JsonArray? results;

    public string? BatchCallId { get; private set; }
    public int? StepIndex { get; private set; }
    public int? StepCount { get; private set; }
    public string? ValidationError { get; private set; }

    public static JsonObject? BuildDefinition(JsonArray tools)
    {
        var names = tools.OfType<JsonObject>().Select(tool => ReadString(tool["name"]))
            .Where(name => name is not null && allowedTools.Contains(name)).ToArray();
        if (names.Length == 0) return null;
        return new JsonObject
        {
            ["type"] = "function", ["name"] = ToolName, ["strict"] = false,
            ["description"] = "Run 2–8 UI tools in order without another model pass. Each step uses the named tool's argument schema and normal guards. Stops on failure or an ambiguous/empty find. Set usePreviousTarget=true on tap/type immediately after find to copy its unique exact target. Use only when the whole short plan is known; return to the model for choices between results. Every step is traced separately.",
            ["parameters"] = new JsonObject
            {
                ["type"] = "object", ["additionalProperties"] = false,
                ["required"] = new JsonArray("steps"),
                ["properties"] = new JsonObject
                {
                    ["steps"] = new JsonObject
                    {
                        ["type"] = "array", ["minItems"] = 2, ["maxItems"] = MaximumSteps,
                        ["items"] = new JsonObject
                        {
                            ["type"] = "object", ["additionalProperties"] = false,
                            ["required"] = new JsonArray("toolName", "arguments"),
                            ["properties"] = new JsonObject
                            {
                                ["toolName"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray(names.Select(name => (JsonNode?)JsonValue.Create(name)).ToArray()) },
                                ["arguments"] = new JsonObject { ["type"] = "object", ["description"] = "Arguments for this tool, using its loaded schema. With usePreviousTarget, omit selector and coordinate fields; only supply action options such as value." },
                                ["usePreviousTarget"] = new JsonObject { ["type"] = "boolean", ["description"] = "For tap/type immediately after find only. Copy the unique returned exact target; stop if it is ambiguous or unavailable. Defaults to false." }
                            }
                        }
                    }
                }
            }
        };
    }

    public IEnumerable<OpenAiFunctionCall> ReadCalls()
    {
        foreach (var call in calls)
        {
            ValidationError = null;
            if (call.Name != ToolName)
            {
                yield return call;
                continue;
            }
            if (!TryParse(call.Arguments, out var steps, out var error))
            {
                ValidationError = error;
                yield return call;
                continue;
            }

            var started = DateTimeOffset.UtcNow;
            var stopwatch = Stopwatch.StartNew();
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
                var completed = results.Count;
                var message = stoppedReason is null
                    ? $"Completed all {steps.Count} batch steps."
                    : $"Batch stopped after {completed} of {steps.Count} steps: {stoppedReason}";
                var output = new JsonObject
                {
                    ["isError"] = failed, ["completed"] = stoppedReason is null,
                    ["requestedStepCount"] = steps.Count, ["executedStepCount"] = completed,
                    ["skippedStepCount"] = steps.Count - completed,
                    ["message"] = message, ["steps"] = results
                }.ToJsonString();
                publishOutput(call.CallId, output);
                onCompleted(new AgentUiBatchCompletion(call, started, stopwatch.ElapsedMilliseconds,
                    new ToolCallResult(failed, output, message)));
            }
            finally
            {
                BatchCallId = null;
                StepIndex = null;
                StepCount = null;
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
            ["message"] = result.Message, ["result"] = ReadObject(result.ModelOutput ?? result.Output)
                ?? (JsonNode?)JsonValue.Create(result.ModelOutput ?? result.Output)
        });
    }

    private bool TryParse(JsonObject arguments, out List<AgentUiBatchStep> steps, out string error)
    {
        steps = [];
        error = "A UI batch must contain 2–8 steps using available UI tools. Nested batches, tasks, and completion calls are not allowed.";
        var available = availableTools.OfType<JsonObject>().Select(tool => ReadString(tool["name"])).ToHashSet();
        if (!available.Contains(ToolName) || arguments["steps"] is not JsonArray { Count: >= 2 and <= MaximumSteps } items)
            return false;
        foreach (var item in items)
        {
            if (item is not JsonObject step || ReadString(step["toolName"]) is not { } name
                || !allowedTools.Contains(name) || !available.Contains(name) || step["arguments"] is not JsonObject input)
                return false;
            var fromPrevious = step["usePreviousTarget"] is JsonValue flag && flag.TryGetValue<bool>(out var enabled) && enabled;
            if (step["usePreviousTarget"] is not null && (step["usePreviousTarget"] is not JsonValue scalar || !scalar.TryGetValue<bool>(out _)))
                return false;
            if (fromPrevious && (name is not ("ansight_tap_ui" or "ansight_type_text")
                                 || steps.LastOrDefault()?.ToolName != "ansight_find_ui"
                                 || input.Any(property => property.Value is not null && !actionOptions.Contains(property.Key))))
            {
                error = "usePreviousTarget is only valid on tap/type immediately after find. Omit selector and coordinate overrides so the exact discovered target is preserved.";
                return false;
            }
            if (name == "ansight_type_text" && ReadString(input["value"]) is null)
            {
                error = "Every type step must include a string value.";
                return false;
            }
            steps.Add(new AgentUiBatchStep(name, input, fromPrevious));
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

internal sealed record AgentUiBatchStep(string ToolName, JsonObject Arguments, bool UsePreviousTarget);
internal sealed record AgentUiBatchCompletion(OpenAiFunctionCall Call, DateTimeOffset StartedUtc, long DurationMilliseconds, ToolCallResult Result);
