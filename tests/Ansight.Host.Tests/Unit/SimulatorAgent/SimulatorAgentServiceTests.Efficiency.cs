using System.Text.Json.Nodes;

namespace Ansight.Host.Tests.Unit.SimulatorAgent;

public sealed partial class SimulatorAgentServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunAsync_FinalScreenshotUsesSameTurnAndFailureFailsCompletion(bool captureFails)
    {
        var completion = CompleteWebSocketTurn();
        completion.FunctionCalls[0].Arguments["captureScreenshot"] = true;
        var gateway = new FakeToolGateway { Result = new(captureFails,
            captureFails ? "{\"isError\":true}" : "{\"result\":{\"artifactPath\":\"final.png\"}}", "Screenshot result") };
        var session = new FakeOpenAiSession([completion]);
        using var service = CreateToolLoadingService(session, gateway);

        var result = await service.RunAsync(ToolLoadingRequest("Inspect and capture the final state.") with { CaptureTrace = true });

        Assert.Equal(captureFails ? SimulatorAgentRunStatus.Failed : SimulatorAgentRunStatus.Succeeded, result.Status);
        Assert.Single(session.Requests);
        Assert.Equal("ansight_take_screenshot", Assert.Single(gateway.Calls).ToolName);
        Assert.Contains(result.Audit.ToolCalls, call => call.ToolName == "ansight_take_screenshot" && call.IsError == captureFails);
        Assert.Contains("screenshot", Assert.Single(result.Audit.ToolCalls, call => call.ToolName == "complete_instruction").Result.Content);
    }

    [Fact]
    public async Task RunAsync_FinalScreenshotDoesNotBypassToolBudget()
    {
        var completion = CompleteWebSocketTurn();
        completion.FunctionCalls[0].Arguments["captureScreenshot"] = true;
        var gateway = new FakeToolGateway();
        AddToolLoadingDefinitions(gateway);
        var session = new FakeOpenAiSession([
            CreateFunctionTurn("observe", "ansight_get_live_visual_tree", new JsonObject()), completion]);
        using var service = CreateToolLoadingService(session, gateway);
        var result = await service.RunAsync(ToolLoadingRequest("Inspect and capture.") with { MaximumToolCalls = 1 });
        Assert.Equal(SimulatorAgentRunStatus.Failed, result.Status);
        Assert.DoesNotContain(gateway.Calls, call => call.ToolName == "ansight_take_screenshot");
    }

    [Fact]
    public void Catalog_DefersDiagnosticsFilesAndExtendedUiButKeepsExtensionsReachable()
    {
        var names = new[] { "ansight_get_logs", "ansight_get_telemetry", "ansight_get_execution_capabilities",
            "ansight_require_capabilities", "ansight_list_sandbox_file", "ansight_read_sandbox_file",
            "ansight_capture_sandbox_file", "ansight_scan_screen", "ansight_scroll_ui", "ansight_find_ui", "extension_tool" };
        var catalogue = new AgentToolCatalog(new JsonArray(names.Select(name => (JsonNode?)new JsonObject
            { ["type"] = "function", ["name"] = name }).ToArray()));
        Assert.Equal(new[] { "extension_tool" }, ToolNames(catalogue.InitialAdditionalTools));
        foreach (var bundle in new[] { "diagnostics", "files", "screen-text", "extended-ui" })
            Assert.False(catalogue.Load(new JsonObject { ["bundle"] = bundle }).Result.IsError);
        Assert.All(names, name => Assert.False(catalogue.IsDeferred(name)));
    }

    [Fact]
    public async Task RunAsync_RepeatedLoadsCannotResetTheNoProgressLimit()
    {
        var gateway = new FakeToolGateway();
        AddToolLoadingDefinitions(gateway, "ansight_find_ui");
        var session = new FakeOpenAiSession(Enumerable.Range(0, 12).Select(index => LoadManualToolsTurn("load-" + index)));
        using var service = CreateToolLoadingService(session, gateway);
        var result = await service.RunAsync(ToolLoadingRequest("Inspect the app.") with { CaptureTrace = true });
        Assert.Equal(SimulatorAgentRunStatus.Failed, result.Status);
        Assert.InRange(session.Requests.Count, 8, 10);
        Assert.Contains(result.Audit.ToolCalls, call => call.Result.Content.Contains("stagnationGuard", StringComparison.Ordinal));
        Assert.Empty(gateway.Calls);
    }

    [Fact]
    public async Task RunAsync_RepeatedPostCompactionLoadFallsBackOnceWithoutReplayingActions()
    {
        var observe = CreateFunctionTurn("find", "ansight_find_ui", new JsonObject { ["automationId"] = "account" });
        observe.Output.Insert(0, new JsonObject { ["type"] = "compaction", ["id"] = "checkpoint", ["encrypted_content"] = "opaque" });
        var gateway = new FakeToolGateway();
        AddToolLoadingDefinitions(gateway, "ansight_find_ui", "ansight_get_logs");
        var session = new FakeOpenAiSession([
            LoadManualToolsTurn("load"), observe, LoadManualToolsTurn("refresh"),
            LoadManualToolsTurn("recover"), CompleteWebSocketTurn()]);
        using var service = CreateToolLoadingService(session, gateway);
        var result = await service.RunAsync(ToolLoadingRequest("Inspect account."));
        Assert.Equal(SimulatorAgentRunStatus.Succeeded, result.Status);
        var recovered = session.Requests[^1];
        Assert.True(recovered.StartNewConversation);
        Assert.Equal("tool-registration-recovery", recovered.ReplayReason);
        Assert.Contains("ansight_get_logs", ToolNames(recovered.Tools));
        Assert.DoesNotContain(AgentToolCatalog.LoadToolName, ToolNames(recovered.Tools));
        Assert.Empty(AdditionalToolItems(recovered.Input));
        Assert.Single(gateway.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunAsync_MultiTaskAssessmentLoadsManualToolsAndRejectsInvalidExclusionsAtomically(bool invalid)
    {
        var declaration = new JsonObject
        {
            ["uncoveredStep"] = "Read the current account label.", ["reason"] = "no-matching-task",
            ["relatedTaskId"] = null, ["evidence"] = "The instruction only requests reading the label.",
            ["consideredTaskIds"] = new JsonArray("save-map", "download-guide"),
            ["taskDecisions"] = new JsonArray(
                new JsonObject { ["taskId"] = "save-map", ["reason"] = "scope-mismatch", ["evidence"] = "Saving a map is not requested." },
                new JsonObject { ["taskId"] = invalid ? "unknown" : "download-guide", ["reason"] = "scope-mismatch", ["evidence"] = "Downloading a guide is not requested." })
        };
        var gateway = new FakeToolGateway { RepositoryTaskShortcuts = [
            CreateRepositoryTaskShortcut("ansight_task_save", "save-map", "Save map"),
            CreateRepositoryTaskShortcut("ansight_task_download", "download-guide", "Download guide")] };
        AddToolLoadingDefinitions(gateway, "ansight_find_ui");
        var turns = new List<OpenAiTurn> { CreateFunctionTurn("declare", "ansight_declare_uncovered_step", declaration) };
        if (invalid)
        {
            var retry = declaration.DeepClone().AsObject();
            retry["taskDecisions"]![1]!["taskId"] = "download-guide";
            turns.Add(CreateFunctionTurn("retry", "ansight_declare_uncovered_step", retry));
        }
        turns.Add(CreateFunctionTurn("find", "ansight_find_ui", new JsonObject { ["automationId"] = "account" }));
        turns.Add(CompleteWebSocketTurn());
        var session = new FakeOpenAiSession(turns);
        using var service = CreateToolLoadingService(session, gateway);
        var result = await service.RunAsync(ToolLoadingRequest("Read the account label.") with { CaptureTrace = true });
        Assert.Equal(invalid, Assert.Single(result.Audit.ToolCalls, call => call.CallId == "declare").IsError);
        if (invalid) Assert.False(Assert.Single(result.Audit.ToolCalls, call => call.CallId == "retry").IsError);
        Assert.Single(gateway.Calls);
        Assert.Contains(AdditionalToolItems(session.Requests[invalid ? 2 : 1].IncrementalInput!),
            item => ToolNames(item["tools"]!.AsArray()).Contains("ansight_find_ui"));
    }

    [Fact]
    public async Task RunAsync_SingleStepBatchExecutesInsteadOfSpendingARepairTurn()
    {
        var gateway = BatchGateway();
        var turn = CreateFunctionTurn("single", AgentUiBatch.ToolName, new JsonObject
        {
            ["steps"] = new JsonArray(new JsonObject { ["toolName"] = "ansight_find_ui", ["arguments"] = new JsonObject { ["automationId"] = "search" } })
        });
        var client = new FakeOpenAiClient([turn, CompleteWebSocketTurn()]);
        using var service = new SimulatorAgentService(new InMemoryEncryptedStorage(), client, gateway);
        service.SetDefaultModelAccessTokenForTesting("sk-local-test");
        var result = await service.RunAsync(BatchRequest());
        Assert.Equal(SimulatorAgentRunStatus.Succeeded, result.Status);
        Assert.Single(gateway.Calls);
        Assert.False(Assert.Single(result.Audit.ToolCalls, call => call.CallId == "single").IsError);
    }

    [Fact]
    public async Task RunAsync_RepeatedTruncationUsesShortFeedbackWithinTheSameContext()
    {
        var observation = new ToolCallResult(false, "{\"result\":{\"capability\":\"ui.observe\",\"truncated\":true}}", "Truncated tree");
        var gateway = new FakeToolGateway { InitialObservation = observation, Result = observation };
        AddToolLoadingDefinitions(gateway);
        var session = new FakeOpenAiSession([
            CreateFunctionTurn("observe", "ansight_get_live_visual_tree", new JsonObject()), CompleteWebSocketTurn()]);
        using var service = CreateToolLoadingService(session, gateway);
        await service.RunAsync(ToolLoadingRequest("Inspect the current page."));
        Assert.Contains("Current observation is truncated; use focused evidence", session.Requests[^1].IncrementalInput!.ToJsonString());
    }

    [Theory]
    [InlineData(false, 2)]
    [InlineData(true, 1)]
    [InlineData(null, 1)]
    public async Task RunAsync_ReassessesOnlyChangedOrUnknownNavigation(bool? changed, int dispatchedActions)
    {
        var action = new JsonObject { ["performed"] = true };
        if (changed.HasValue) action["navigationChanged"] = changed.Value;
        var gateway = new FakeToolGateway
        {
            RepositoryTaskShortcuts = [CreateRepositoryTaskShortcut("ansight_task_weather", "weather", "Read weather")],
            Result = new(false, new JsonObject { ["result"] = action }.ToJsonString(), "Tapped")
        };
        var declaration = new JsonObject
        {
            ["uncoveredStep"] = "Open the two account controls.", ["reason"] = "no-matching-task", ["relatedTaskId"] = null,
            ["evidence"] = "Only weather is automated.", ["consideredTaskIds"] = new JsonArray("weather")
        };
        var client = new FakeOpenAiClient([
            CreateFunctionTurn("declare", "ansight_declare_uncovered_step", declaration),
            CreateFunctionTurn("tap1", "ansight_tap_ui", new JsonObject { ["automationId"] = "control1" }),
            CreateFunctionTurn("tap2", "ansight_tap_ui", new JsonObject { ["automationId"] = "control2" }), CompleteWebSocketTurn()]);
        using var service = new SimulatorAgentService(new InMemoryEncryptedStorage(), client, gateway);
        service.SetDefaultModelAccessTokenForTesting("sk-local-test");
        await service.RunAsync(new SimulatorAgentRunRequest("session-123", ["Open account controls."]));
        Assert.Equal(dispatchedActions, gateway.Calls.Count);
    }
}
