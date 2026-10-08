using System.Text.Json.Nodes;

namespace Ansight.Host.Tests.Unit.SimulatorAgent;

public sealed partial class SimulatorAgentServiceTests
{
    [Theory]
    [InlineData("ansight_task_copy")]
    [InlineData("ansight_run_task")]
    public async Task RunAsync_PassedTaskCanCompleteOnNextPassWithPriorEvidenceAndUnusedShortcuts(string toolName)
    {
        var gateway = new FakeToolGateway
        {
            RepositoryTaskShortcuts =
            [
                CreateRepositoryTaskShortcut("ansight_task_copy", "copy-gps-location", "Copy GPS location"),
                CreateRepositoryTaskShortcut("ansight_task_weather", "load-weather", "Load weather")
            ],
            InitialObservation = new ToolCallResult(false,
                """{"result":{"capability":"ui.observe","root":{"text":"Eagle Rock","type":"AreaPage","visible":true}}}""",
                "Eagle Rock detail page is visible."),
            Result = new ToolCallResult(false,
                """{"status":"Passed","assertions":[{"assertionId":"clipboard-matches-displayed-gps","passed":true}]}""",
                "Task passed its clipboard assertion.")
        };
        AddToolLoadingDefinitions(gateway);
        var arguments = toolName == "ansight_run_task"
            ? new JsonObject { ["taskId"] = "copy-gps-location", ["input"] = new JsonObject() }
            : new JsonObject();
        var session = new FakeOpenAiSession([
            CreateFunctionTurn("copy", toolName, arguments),
            CompleteWebSocketTurn()
        ]);
        using var service = CreateToolLoadingService(session, gateway);

        var result = await service.RunAsync(ToolLoadingRequest(
            "Verify the Eagle Rock detail page is open, then copy its GPS location and check the clipboard matches."));

        Assert.Equal(SimulatorAgentRunStatus.Succeeded, result.Status);
        Assert.Equal(2, session.Requests.Count);
        Assert.Equal("ansight_run_task", Assert.Single(gateway.Calls).ToolName);
        var afterTask = session.Requests[1].IncrementalInput!;
        AssertRepositoryTaskState(Assert.Single(RepositoryTaskStates(afterTask)), ["load-weather"], [], [], true);
        var feedback = afterTask.ToJsonString();
        Assert.Contains("The repository task passed.", feedback, StringComparison.Ordinal);
        Assert.Contains("call complete_instruction now", feedback, StringComparison.Ordinal);
        Assert.Contains("specific requested action or check that remains unproven", feedback, StringComparison.Ordinal);
        Assert.True(feedback.IndexOf("The repository task passed.", StringComparison.Ordinal)
                    > feedback.IndexOf("Repository task state:", StringComparison.Ordinal));
        Assert.Contains("Eagle Rock", session.Requests[1].Input.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_OrdinaryToolSuccessDoesNotReceivePassedTaskGuidance()
    {
        var gateway = new FakeToolGateway
        {
            Result = new ToolCallResult(false, "{\"matches\":[]}", "No matches.")
        };
        AddToolLoadingDefinitions(gateway);
        var session = new FakeOpenAiSession([
            CreateFunctionTurn("observe", "ansight_get_live_visual_tree", new JsonObject()),
            CompleteWebSocketTurn()
        ]);
        using var service = CreateToolLoadingService(session, gateway);

        await service.RunAsync(ToolLoadingRequest("Inspect the current screen."));

        Assert.DoesNotContain("The repository task passed.",
            session.Requests[1].IncrementalInput!.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_EmitsTaskStateOnlyWhenSuccessChangesRemainingTasks()
    {
        var gateway = new FakeToolGateway
        {
            RepositoryTaskShortcuts =
            [
                CreateRepositoryTaskShortcut("ansight_task_account", "open-account", "Open account"),
                CreateRepositoryTaskShortcut("ansight_task_profile", "open-profile", "Open profile")
            ],
            Result = new ToolCallResult(false, "{\"status\":\"Passed\"}", "Task passed.")
        };
        AddToolLoadingDefinitions(gateway);
        var session = new FakeOpenAiSession([
            CreateFunctionTurn("first", "ansight_task_account", new JsonObject()),
            CreateFunctionTurn("repeat", "ansight_task_account", new JsonObject()),
            CreateFunctionTurn("second", "ansight_task_profile", new JsonObject()),
            CompleteWebSocketTurn()
        ]);
        using var service = CreateToolLoadingService(session, gateway);

        var result = await service.RunAsync(ToolLoadingRequest("Open account, then open profile."));

        Assert.Equal(SimulatorAgentRunStatus.Succeeded, result.Status);
        Assert.Equal(3, gateway.Calls.Count);
        AssertRepositoryTaskState(Assert.Single(RepositoryTaskStates(session.Requests[1].IncrementalInput!)),
            ["open-profile"], [], [], true);
        Assert.Empty(RepositoryTaskStates(session.Requests[2].IncrementalInput!));
        AssertRepositoryTaskState(Assert.Single(RepositoryTaskStates(session.Requests[3].IncrementalInput!)),
            [], [], [], false);
        Assert.Equal(2, session.IncrementalInputs.Sum(input => RepositoryTaskStates(input).Count()));
        Assert.Contains("continue uncovered residual work without replaying a passed prefix",
            session.Requests[0].Instructions, StringComparison.Ordinal);
        Assert.All(session.IncrementalInputs, input =>
        {
            Assert.DoesNotContain("If it covers only a prefix", input.ToJsonString(), StringComparison.Ordinal);
            Assert.DoesNotContain("Before any further manual UI action", input.ToJsonString(), StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task RunAsync_TaskFailureKeepsFailureEvidenceAndStateUntilSuccessfulRetry()
    {
        var gateway = new FakeToolGateway
        {
            RepositoryTaskShortcuts =
            [CreateRepositoryTaskShortcut("ansight_task_account", "open-account", "Open account")]
        };
        gateway.Results.Enqueue(new ToolCallResult(true,
            "{\"isError\":true,\"message\":\"Account visibility assertion failed.\"}",
            "Account visibility assertion failed."));
        gateway.Results.Enqueue(new ToolCallResult(false, "{\"status\":\"Passed\"}", "Account is visible."));
        AddToolLoadingDefinitions(gateway);
        var session = new FakeOpenAiSession([
            CreateFunctionTurn("failed", "ansight_task_account", new JsonObject()),
            CreateFunctionTurn("retry", "ansight_task_account", new JsonObject()),
            CompleteWebSocketTurn()
        ]);
        using var service = CreateToolLoadingService(session, gateway);

        var result = await service.RunAsync(ToolLoadingRequest("Open account."));

        Assert.Equal(SimulatorAgentRunStatus.Succeeded, result.Status);
        var afterFailure = session.Requests[1].IncrementalInput!;
        AssertRepositoryTaskState(Assert.Single(RepositoryTaskStates(afterFailure)), [], ["open-account"], [], true);
        Assert.Contains("The repository task did not complete successfully.", afterFailure.ToJsonString(), StringComparison.Ordinal);
        Assert.DoesNotContain("The repository task passed.", afterFailure.ToJsonString(), StringComparison.Ordinal);
        var failureOutput = Assert.Single(afterFailure.OfType<JsonObject>(), item =>
            item["type"]?.GetValue<string>() == "function_call_output" && item["call_id"]?.GetValue<string>() == "failed");
        Assert.Contains("Account visibility assertion failed.", failureOutput["output"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.True(Assert.Single(result.Audit.ToolCalls, call => call.CallId == "failed").IsError);
        AssertRepositoryTaskState(Assert.Single(RepositoryTaskStates(session.Requests[2].IncrementalInput!)), [], [], [], false);
        Assert.DoesNotContain("The repository task did not complete successfully.",
            session.Requests[2].IncrementalInput!.ToJsonString(), StringComparison.Ordinal);
        Assert.Contains("The repository task passed.",
            session.Requests[2].IncrementalInput!.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_TaskStateTracksAcceptedExclusionsWithoutRelaxingPartialSuccessGuard()
    {
        var gateway = new FakeToolGateway
        {
            RepositoryTaskShortcuts =
            [
                CreateRepositoryTaskShortcut("ansight_task_account", "open-account", "Open account"),
                CreateRepositoryTaskShortcut("ansight_task_export", "export-account-data", "Export account data")
            ]
        };
        gateway.Results.Enqueue(new ToolCallResult(false, "{\"status\":\"Passed\"}", "Task passed."));
        gateway.Results.Enqueue(new ToolCallResult(false, "{\"performed\":true}", "Overlay dismissed."));
        AddToolLoadingDefinitions(gateway, "ansight_tap_ui");
        var session = new FakeOpenAiSession([
            LoadManualToolsTurn("load"),
            CreateFunctionTurn("first", "ansight_task_account", new JsonObject()),
            CreateFunctionTurn("blocked", "ansight_tap_ui", new JsonObject { ["automationId"] = "dismiss-overlay" }),
            CreateFunctionTurn("exclude", "ansight_declare_uncovered_step", new JsonObject
            {
                ["uncoveredStep"] = "Dismiss the welcome overlay.",
                ["reason"] = "scope-mismatch",
                ["relatedTaskId"] = "export-account-data",
                ["evidence"] = "The instruction only opens account and dismisses the overlay; exporting data exceeds its scope.",
                ["consideredTaskIds"] = new JsonArray("export-account-data")
            }),
            CreateFunctionTurn("allowed", "ansight_tap_ui", new JsonObject { ["automationId"] = "dismiss-overlay" }),
            CompleteWebSocketTurn()
        ]);
        using var service = CreateToolLoadingService(session, gateway);

        var result = await service.RunAsync(ToolLoadingRequest("Open account and dismiss the welcome overlay."));

        Assert.Equal(SimulatorAgentRunStatus.Succeeded, result.Status);
        AssertRepositoryTaskState(Assert.Single(RepositoryTaskStates(session.Requests[2].IncrementalInput!)),
            ["export-account-data"], [], [], true);
        var blocked = Assert.Single(result.Audit.ToolCalls, call => call.CallId == "blocked");
        Assert.True(blocked.IsError);
        Assert.Contains("reassessment is required", blocked.Message, StringComparison.Ordinal);
        Assert.Contains("export-account-data", blocked.Message, StringComparison.Ordinal);
        Assert.Empty(RepositoryTaskStates(session.Requests[3].IncrementalInput!));
        Assert.False(Assert.Single(result.Audit.ToolCalls, call => call.CallId == "exclude").IsError);
        AssertRepositoryTaskState(Assert.Single(RepositoryTaskStates(session.Requests[4].IncrementalInput!)),
            [], [], ["export-account-data"], false);
        Assert.False(Assert.Single(result.Audit.ToolCalls, call => call.CallId == "allowed").IsError);
        Assert.Empty(RepositoryTaskStates(session.Requests[5].IncrementalInput!));
        Assert.Collection(gateway.Calls,
            call => Assert.Equal("ansight_run_task", call.ToolName),
            call => Assert.Equal("ansight_tap_ui", call.ToolName));
    }

    private static IEnumerable<JsonObject> RepositoryTaskStates(JsonArray input)
    {
        const string prefix = "Repository task state: ";
        foreach (var item in input.OfType<JsonObject>())
        {
            if (item["role"]?.GetValue<string>() != "user" || item["content"] is not JsonArray content)
            {
                continue;
            }
            foreach (var part in content.OfType<JsonObject>())
            {
                if (part["text"]?.GetValue<string>() is { } text && text.StartsWith(prefix, StringComparison.Ordinal))
                {
                    yield return Assert.IsType<JsonObject>(JsonNode.Parse(text[prefix.Length..]));
                }
            }
        }
    }

    private static void AssertRepositoryTaskState(
        JsonObject state, string[] remainingTaskIds, string[] failedTaskIds, string[] excludedTaskIds, bool reassessmentRequired)
    {
        Assert.Equal(remainingTaskIds, ReadIds("remainingTaskIds"));
        Assert.Equal(failedTaskIds, ReadIds("failedTaskIds"));
        Assert.Equal(excludedTaskIds, ReadIds("excludedTaskIds"));
        Assert.Equal(reassessmentRequired, state["reassessmentRequired"]?.GetValue<bool>());

        string[] ReadIds(string propertyName)
            => Assert.IsType<JsonArray>(state[propertyName]).Select(value => value!.GetValue<string>()).ToArray();
    }
}
