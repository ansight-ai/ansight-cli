using System.Text.Json.Nodes;

namespace Ansight.Host.Tests.Unit.SimulatorAgent;

public sealed partial class SimulatorAgentServiceTests
{
    [Theory]
    [InlineData("ansight_get_live_visual_tree")]
    [InlineData("ansight_evaluate_screenshot")]
    public async Task CoverageOnlyReportCannotMoveCurrentStateAndCorrectionsConsumeFreshObservation(string observationTool)
    {
        var client = new FakeOpenAiClient(
            [
                ScrollScopeProgress("initial", "home"),
            ScrollScopeProgress("coverage-jump", "other"),
            CreateFunctionTurn("inspect", observationTool, observationTool == "ansight_get_live_visual_tree"
                ? new JsonObject {["toolId"] = "ui.get_visual_tree", ["root"] = "window" }                : new JsonObject {["image"] = new JsonObject {["frameId"] = "foreground" }}),
            ScrollScopeProgress("missing-evidence", "dialog"),
            ScrollScopeProgress("wrong-observation", "dialog", observationId: "invented"),
            ScrollScopeProgress("correct", "dialog", observationId: "inspect"),
            ScrollScopeProgress("replayed-observation", "home", observationId: "inspect"),
            CreateFunctionTurn("close", "ansight_tap_ui", new JsonObject {["automationId"] = "dialog-close" }),
            CreateFunctionTurn("stop", "complete_instruction", new JsonObject
            {
                ["outcome"] = "partial",
                ["summary"] = "Pending close outcome is retained."
            })
            ]);
        using var service = new SimulatorAgentService(new InMemoryEncryptedStorage(), client, new FakeToolGateway());
        service.SetDefaultModelAccessTokenForTesting("sk-local-test");

        var result = await service.RunAsync(new SimulatorAgentRunRequest("session-123", ["Explore the app."])
        {
            AppGraphExplorationName = "Default"
        });

        foreach (var id in new[] { "coverage-jump", "missing-evidence", "wrong-observation", "replayed-observation" })
        {
            Assert.True(Assert.Single(result.Audit.ToolCalls, call => call.CallId == id).IsError);
        }

        Assert.False(Assert.Single(result.Audit.ToolCalls, call => call.CallId == "correct").IsError);

        var run = Assert.Single(service.ListAppGraphLiveRuns("session-123"));
        Assert.Equal("dialog", run.PendingOutcome!.FromDestinationId);
        Assert.DoesNotContain(run.Nodes, node => node.Id == "other");
    }

    [Theory]
    [InlineData("ansight_find_ui", "{}")]
    [InlineData("ansight_scan_screen", "{}")]
    [InlineData("ansight_wait_for_ui", "{}")]
    [InlineData("ansight_assert_ui", "{}")]
    [InlineData("ansight_get_live_visual_tree", "{\"toolId\":\"maui.get_visual_tree\"}")]
    [InlineData("ansight_get_live_visual_tree", "{\"toolId\":\"ui.get_visual_tree\",\"rootNodeId\":\"background\"}")]
    public async Task FocusedOrLogicalEvidenceCannotReplaceForegroundState(string tool, string arguments)
    {
        var client = new FakeOpenAiClient(
            [
                ScrollScopeProgress("initial", "dialog"),
            CreateFunctionTurn("background-match", tool, JsonNode.Parse(arguments)!.AsObject()),
            ScrollScopeProgress("invalid-correction", "home", observationId: "background-match"),
            CreateFunctionTurn("stop", "complete_instruction", new JsonObject
            {
                ["outcome"] = "partial",
                ["summary"] = "A background match does not dismiss the dialog."
            })
            ]);
        using var service = new SimulatorAgentService(new InMemoryEncryptedStorage(), client, new FakeToolGateway());
        service.SetDefaultModelAccessTokenForTesting("sk-local-test");

        var result = await service.RunAsync(new SimulatorAgentRunRequest("session-123", ["Explore."])
        {
            AppGraphExplorationName = "Default"
        });

        var correction = Assert.Single(result.Audit.ToolCalls, call => call.CallId == "invalid-correction");
        Assert.True(correction.IsError);
        Assert.Contains("Focused matches and OCR excerpts", correction.Result.Content);
        Assert.Equal("dialog", Assert.Single(service.ListAppGraphLiveRuns("session-123")).CurrentDestinationId);
    }

    [Fact]
    public async Task FocusedQueryInvalidatesEarlierForegroundCorrectionEvidence()
    {
        var client = new FakeOpenAiClient(
            [
                ScrollScopeProgress("initial", "dialog"),
            CreateFunctionTurn("full-tree", "ansight_get_live_visual_tree", new JsonObject
            {
                ["toolId"] = "ui.get_visual_tree",
                ["root"] = "window"
            }),
            CreateFunctionTurn("background-match", "ansight_find_ui", new JsonObject {["text"] = "Routes" }),
            ScrollScopeProgress("stale-correction", "home", observationId: "full-tree"),
            CreateFunctionTurn("stop", "complete_instruction", new JsonObject
            {
                ["outcome"] = "partial",
                ["summary"] = "Foreground correction needs the latest full observation."
            })
            ]);
        using var service = new SimulatorAgentService(new InMemoryEncryptedStorage(), client, new FakeToolGateway());
        service.SetDefaultModelAccessTokenForTesting("sk-local-test");

        var result = await service.RunAsync(new SimulatorAgentRunRequest("session-123", ["Explore."])
        {
            AppGraphExplorationName = "Default"
        });

        Assert.True(Assert.Single(result.Audit.ToolCalls, call => call.CallId == "stale-correction").IsError);
        Assert.Equal("dialog", Assert.Single(service.ListAppGraphLiveRuns("session-123")).CurrentDestinationId);
    }

    [Theory]
    [InlineData("ansight_find_ui")]
    [InlineData("ansight_evaluate_screenshot")]
    public async Task UnverifiedOutcomeKeepsInputLockedUntilExplicitlyDeferred(string observationTool)
    {
        var gateway = new FakeToolGateway();
        var client = new FakeOpenAiClient(
            [
            ScrollScopeProgress("initial", "home"),
            CreateFunctionTurn("open", "ansight_tap_ui", new JsonObject {["automationId"] = "open" }),
            ScrollScopeProgress("uncertain", "home", "open", "needs_verification"),
            CreateFunctionTurn("guarded", "ansight_tap_ui", new JsonObject {["automationId"] = "another" }),
            CreateFunctionTurn("inspect", observationTool, observationTool == "ansight_evaluate_screenshot"
                ? new JsonObject
                {
                    ["image"] = new JsonObject {["frameId"] = "after-input" },
                    ["criteria"] = new JsonArray(new JsonObject
                    {
                        ["id"] = "closed",
                        ["description"] = "The editor is closed."
                    })
                }                : new JsonObject {["automationId"] = "another" }),
            ScrollScopeProgress("defer", "home", "open", "deferred"),
            CreateFunctionTurn("next", "ansight_tap_ui", new JsonObject {["automationId"] = "another" }),
            CreateFunctionTurn("stop", "complete_instruction", new JsonObject
            {
                ["outcome"] = "partial",
                ["summary"] = "The first action is deferred; the second still needs verification."
            })
        ]);
        using var service = new SimulatorAgentService(new InMemoryEncryptedStorage(), client, gateway);
        service.SetDefaultModelAccessTokenForTesting("sk-local-test");

        var result = await service.RunAsync(new SimulatorAgentRunRequest("session-123", ["Explore the app."])
        {
            AppGraphExplorationName = "Default"
        });

        Assert.Equal(2, gateway.Calls.Count(call => call.ToolName == "ansight_tap_ui"));
        Assert.Contains(gateway.Calls, call => call.ToolName == observationTool);
        Assert.False(Assert.Single(result.Audit.ToolCalls, call => call.CallId == "inspect").IsError);
        Assert.True(Assert.Single(result.Audit.ToolCalls, call => call.CallId == "guarded").IsError);
        Assert.False(Assert.Single(result.Audit.ToolCalls, call => call.CallId == "defer").IsError);

        var run = Assert.Single(service.ListAppGraphLiveRuns("session-123"));
        Assert.Equal("next", run.PendingOutcome!.InputId);
        Assert.Contains(run.Frontier, action => action.AutomationId == "open" && action.Status == "deferred");
    }

    [Theory]
    [InlineData("ansight_tap_ui")]
    [InlineData("ansight_scroll_ui")]
    [InlineData("ansight_dismiss_keyboard")]
    public async Task GraphRequiresStateReconciliationBeforeFurtherInputButAllowsObservation(string inputTool)
    {
        var client = new FakeOpenAiClient(
            [
                CreateFunctionTurn("unobserved", inputTool, new JsonObject {["automationId"] = "open" }),
            ScrollScopeProgress("observe-home", "home"),
            CreateFunctionTurn("open", inputTool, new JsonObject {["automationId"] = "open" }),
            CreateFunctionTurn("unreported", "ansight_tap_ui", new JsonObject {["automationId"] = "close" }),
            CreateFunctionTurn("inspect", "ansight_find_ui", new JsonObject {["automationId"] = "close" }),
            ScrollScopeProgress("observe-dialog", "dialog", "open", "verified"),
            CreateFunctionTurn("close", "ansight_tap_ui", new JsonObject {["automationId"] = "close" }),
            CreateFunctionTurn("stop", "complete_instruction", new JsonObject
            {
                ["outcome"] = "failed",
                ["summary"] = "Coverage remains unverified."
            })
            ]);
        var gateway = new FakeToolGateway();
        using var service = new SimulatorAgentService(new InMemoryEncryptedStorage(), client, gateway);
        service.SetDefaultModelAccessTokenForTesting("sk-local-test");

        var result = await service.RunAsync(new SimulatorAgentRunRequest("session-123", ["Explore the app."])
        {
            AppGraphExplorationName = "Default"
        });

        Assert.Equal(2, gateway.Calls.Count(call => call.ToolName == "ansight_tap_ui" || call.ToolName == inputTool));
        Assert.Contains(gateway.Calls, call => call.ToolName == "ansight_find_ui");
        Assert.All(result.Audit.ToolCalls.Where(call => call.CallId is "unobserved" or "unreported"), call =>
        {
            Assert.True(call.IsError);
            Assert.Contains("appGraphStateReconciliationGuard", call.Result.Content);
        });

        var run = Assert.Single(service.ListAppGraphLiveRuns("session-123"));
        Assert.Null(run.CurrentDestinationId);

        if (inputTool == "ansight_tap_ui")
        {
            Assert.Contains(run.Frontier, action => action.AutomationId == "open" && action.DestinationId == "home");
        }

        Assert.Contains(run.Frontier, action => action.AutomationId == "close" && action.DestinationId == "dialog");
        Assert.DoesNotContain(run.Frontier, action => action.AutomationId == "close" && action.DestinationId == "home");
    }

    [Fact]
    public async Task RecreatedScrollContainerCannotResetItsGuard()
    {
        var turns = new List<OpenAiTurn>();

        for (var index = 0; index < 3; index++)
        {
            turns.Add(ScrollScopeProgress($"observe-{index}", "sheet", index == 0 ? null : $"scroll-{index - 1}"));
            turns.Add(CreateFunctionTurn($"scroll-{index}", "ansight_scroll_ui", new JsonObject
            {
                ["nodeId"] = $"runtime-{index}",
                ["ancestorAutomationId"] = "sheet",
                ["role"] = "scrollview",
                ["type"] = "NativeList",
                ["orientation"] = "down"
            }));
        }

        turns.Add(CreateFunctionTurn("stop", "complete_instruction", new JsonObject
        {
            ["outcome"] = "failed",
            ["summary"] = "Coverage remains unverified."
        }));

        var gateway = new FakeToolGateway();
        using var service = new SimulatorAgentService(new InMemoryEncryptedStorage(), new FakeOpenAiClient(turns), gateway);
        service.SetDefaultModelAccessTokenForTesting("sk-local-test");

        var result = await service.RunAsync(new SimulatorAgentRunRequest("session-123", ["Explore the app."])
        {
            AppGraphExplorationName = "Default"
        });

        Assert.Equal(2, gateway.Calls.Count(call => call.ToolName == "ansight_scroll_ui"));
        Assert.Contains("appGraphScrollGuard", Assert.Single(result.Audit.ToolCalls, call => call.CallId == "scroll-2").Result.Content);
    }
}
