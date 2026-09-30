using System.Text.Json.Nodes;
using Ansight.Tools;

namespace Ansight.Host.Runtime.Operations.Tools.UiAutomation;

internal sealed class AssertLiveUiTool : RemoteAppOperation
{
    public AssertLiveUiTool(OperationServices services)
        : base(services)
    {
    }

    public override string Name => "ansight_assert_ui";

    protected override string Title => "Assert Live UI";

    protected override string Description => "Assert live UI presence, absence, count, text, value, visibility, or enabled state against a visual-tree selector.";

    protected override JsonObject InputSchema => ToolSchema.Object(
        properties: LiveUiToolSchemas.SelectorProperties(
            new Dictionary<string, ToolSchema>
            {
                ["sessionId"] = ToolSchema.String("Specific live session id to target.", nullable: true),
                ["appId"] = ToolSchema.String("App id to target when exactly one live session exists for that app.", nullable: true),
                ["exists"] = ToolSchema.Boolean("Whether at least one matching node must exist. Defaults to true.", nullable: true),
                ["expectedCount"] = ToolSchema.Integer("Exact expected match count.", nullable: true),
                ["expectedText"] = ToolSchema.String("Exact expected text on the selected match.", nullable: true),
                ["expectedValue"] = ToolSchema.String("Exact expected visual or control value on the selected match.", nullable: true),
                ["expectedVisible"] = ToolSchema.Boolean("Expected visibility on the selected match.", nullable: true),
                ["expectedEnabled"] = ToolSchema.Boolean("Expected enabled state on the selected match.", nullable: true),
                ["actionId"] = ToolSchema.String("Optional UI action id to correlate this assertion with.", nullable: true)
            }),
        additionalProperties: false).ToJson();

    public override async Task<RequestResult> ExecuteAsync(JsonObject? arguments, string? correlationId)
    {
        if (!sessionResolver.TryResolveLiveSession(arguments, out var snapshot, out var resolutionError))
        {
            return ToolError(resolutionError);
        }

        var selector = LiveUiSelector.Parse(arguments);
        if (!selector.HasCriteria)
        {
            return ToolError("At least one stable UI selector is required.");
        }

        var captureResult = await LiveUiTreeCapture.CaptureForSelectorAsync(
            snapshot!,
            appToolBridge,
            uiInputRouter,
            Name,
            correlationId,
            selector,
            ToolExecutionCancellation.Current);
        if (!captureResult.IsSuccess || captureResult.Capture is null)
        {
            return ToolError(captureResult.Message);
        }

        var capture = captureResult.Capture;
        var matches = LiveUiNodeQuery.OrderMatchesForSelection(
            LiveUiNodeQuery.Find(capture.Root, selector, capture.TypeRegistry),
            capture.Payload,
            capture.Viewport);
        var expectedCount = ReadNullableInteger(arguments, "expectedCount");
        var expectedExists = ReadNullableBoolean(arguments, "exists")
                             ?? expectedCount != 0;
        var selected = selector.Select(matches);
        var failures = new JsonArray();

        var selectedExists = selector.IndexSpecified
            ? selector.IsAvailable(matches.Count)
            : matches.Count > 0;
        if (expectedExists != selectedExists)
        {
            failures.Add(selector.IndexSpecified
                ? expectedExists
                    ? $"Expected match index {selector.Index}, but only {matches.Count} matching node(s) were found."
                    : $"Expected match index {selector.Index} to be absent, but it exists."
                : expectedExists
                    ? "Expected at least one matching node, but none was found."
                    : $"Expected no matching nodes, but found {matches.Count}.");
        }

        if (expectedCount.HasValue && matches.Count != expectedCount.Value)
        {
            failures.Add($"Expected {expectedCount.Value} matching node(s), but found {matches.Count}.");
        }

        if (HasSelectedAssertions(arguments) && selected is null)
        {
            failures.Add($"Match index {selector.Index} is unavailable for selected-node assertions.");
        }
        else if (selected is not null)
        {
            AssertString(failures, "text", LiveUiNodeQuery.ReadText(selected.Node), LiveUiToolSchemas.ReadString(arguments, "expectedText"));
            AssertString(failures, "value", LiveUiNodeQuery.ReadValue(selected.Node), LiveUiToolSchemas.ReadString(arguments, "expectedValue"));
            AssertBoolean(failures, "visible", LiveUiNodeQuery.IsEffectivelyVisible(selected), ReadNullableBoolean(arguments, "expectedVisible"));
            AssertBoolean(failures, "enabled", LiveUiNodeQuery.IsEffectivelyEnabled(selected), ReadNullableBoolean(arguments, "expectedEnabled"));
        }

        var passed = failures.Count == 0;
        var actionId = LiveUiToolSchemas.ReadString(arguments, "actionId");
        if (actionId is not null)
        {
            runtimeState.AddSessionLog(
                capture.Session.SessionId,
                new LogEntry(
                    DateTimeOffset.UtcNow,
                    passed ? "UI assertion passed." : $"UI assertion failed: {string.Join(" ", failures.Select(failure => failure?.ToString()))}")
                {
                    Source = "Ansight UI Assertion",
                    Tag = "ui.assert",
                    EventId = actionId,
                    Priority = passed ? LogPriority.Information : LogPriority.Error
                });
        }

        return RequestResult.ToolResult(
            new JsonObject
            {
                ["capability"] = "ui.assert",
                ["actionId"] = actionId,
                ["passed"] = passed,
                ["sessionId"] = capture.Session.SessionId,
                ["appId"] = capture.Session.AppId,
                ["visualTreeToolId"] = capture.ToolId,
                ["capturedAtUtc"] = capture.CapturedAtUtc,
                ["selector"] = selector.ToJson(),
                ["matchCount"] = matches.Count,
                ["selected"] = selected is null ? null : LiveUiNodeQuery.ToResultJson(selected),
                ["failures"] = failures
            },
            isError: !passed);
    }

    private static bool HasSelectedAssertions(JsonObject? arguments)
        => arguments?["expectedText"] is not null
           || arguments?["expectedValue"] is not null
           || arguments?["expectedVisible"] is not null
           || arguments?["expectedEnabled"] is not null;

    private static void AssertString(JsonArray failures, string name, string? actual, string? expected)
    {
        if (expected is not null && !string.Equals(actual, expected, StringComparison.Ordinal))
        {
            failures.Add($"Expected {name} '{expected}', but found '{actual ?? "<null>"}'.");
        }
    }

    private static void AssertBoolean(JsonArray failures, string name, bool actual, bool? expected)
    {
        if (expected.HasValue && actual != expected.Value)
        {
            failures.Add($"Expected {name}={expected.Value.ToString().ToLowerInvariant()}, but found {actual.ToString().ToLowerInvariant()}.");
        }
    }

    private static bool? ReadNullableBoolean(JsonObject? arguments, string propertyName)
        => arguments?[propertyName] is JsonValue value && value.TryGetValue<bool>(out var result)
            ? result
            : null;

    private static int? ReadNullableInteger(JsonObject? arguments, string propertyName)
        => arguments?[propertyName] is JsonValue value && value.TryGetValue<int>(out var result)
            ? result
            : null;
}
