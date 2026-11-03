using System.Text.Json.Nodes;

namespace Ansight.Host.Tests.Unit.SimulatorAgent;

public sealed partial class SimulatorAgentServiceTests
{
    [Fact]
    public async Task ScrollGuardSurvivesSelectorAliasesAndTapsButSeparatesDestinationsAndDirections()
    {
        var client = new FakeOpenAiClient(
        [
            ScrollScopeProgress("first-report", "home"),
            CreateFunctionTurn("first", "ansight_scroll_ui", new JsonObject
            {
                ["automationId"] = "list", ["role"] = "scrollview", ["orientation"] = "down", ["direction"] = "down"
            }),
            ScrollScopeProgress("after-first-scroll", "home", "first"),
            CreateFunctionTurn("tap", "ansight_tap_ui", new JsonObject { ["automationId"] = "home-tab" }),
            ScrollScopeProgress("same-report", "home", "tap", "verified"),
            CreateFunctionTurn("second", "ansight_scroll_ui", new JsonObject
            {
                ["automationId"] = "list", ["nodeId"] = "fresh-id", ["orientation"] = "S"
            }),
            ScrollScopeProgress("after-second-scroll", "home", "second"),
            CreateFunctionTurn("guarded", "ansight_scroll_ui", new JsonObject
            {
                ["automationId"] = "list", ["direction"] = "south"
            }),
            CreateFunctionTurn("opposite", "ansight_scroll_ui", new JsonObject
            {
                ["automationId"] = "list", ["orientation"] = "up"
            }),
            ScrollScopeProgress("other-report", "details", "opposite"),
            CreateFunctionTurn("other-destination", "ansight_scroll_ui", new JsonObject
            {
                ["automationId"] = "list", ["orientation"] = "down"
            }),
            CreateFunctionTurn("stop", "complete_instruction", new JsonObject
            {
                ["outcome"] = "failed", ["summary"] = "Coverage remains unverified."
            })
        ]);
        var gateway = new FakeToolGateway();
        gateway.ToolDefinitions.Add(new JsonObject
        {
            ["type"] = "function", ["name"] = "ansight_scroll_ui",
            ["parameters"] = new JsonObject { ["type"] = "object" }
        });
        using var service = new SimulatorAgentService(new InMemoryEncryptedStorage(), client, gateway);
        service.SetDefaultModelAccessTokenForTesting("sk-local-test");

        var result = await service.RunAsync(new SimulatorAgentRunRequest("session-123", ["Explore the app."])
        {
            AppGraphExplorationName = "Default"
        });

        Assert.Equal(4, gateway.Calls.Count(call => call.ToolName == "ansight_scroll_ui"));
        var guard = Assert.Single(result.Audit.ToolCalls, call => call.CallId == "guarded");
        Assert.True(guard.IsError);
        Assert.Contains("2 consecutive attempts without evidence", guard.Message);
        Assert.False(Assert.Single(result.Audit.ToolCalls, call => call.CallId == "opposite").IsError);
        Assert.False(Assert.Single(result.Audit.ToolCalls, call => call.CallId == "other-destination").IsError);
    }

    private static OpenAiTurn ScrollScopeProgress(string callId, string destinationId, string? previousInputId = null,
        string outcome = "deferred", string? observationId = null)
    {
        return CreateFunctionTurn(callId, "report_app_graph_progress", new JsonObject
        {
            ["message"] = "Observed the current destination.",
            ["currentDestinationId"] = destinationId,
            ["actionOutcome"] = previousInputId is null ? null : GraphOutcome(previousInputId, outcome),
            ["stateObservation"] = observationId is null ? null : new JsonObject
            {
                ["observationId"] = observationId, ["evidence"] = "Fresh foreground content identifies the corrected state."
            },
            ["destinations"] = new JsonArray(new JsonObject
            {
                ["id"] = destinationId, ["kind"] = "screen", ["name"] = destinationId,
                ["purpose"] = "Inspect content.", ["description"] = "Content is visible.",
                ["scrollStatus"] = "in_progress"
            }),
            ["actions"] = new JsonArray(), ["transitions"] = new JsonArray(),
            ["coverage"] = new JsonObject { ["gaps"] = new JsonArray() }
        });
    }
    private static JsonObject GraphOutcome(string inputId, string status = "verified") => new()
    {
        ["inputId"] = inputId, ["actionId"] = "", ["status"] = status,
        ["evidenceRefs"] = new JsonArray(inputId),
        ["evidence"] = "After-state content was observed in the retained input evidence.",
        ["reason"] = status == "verified" ? "" : "Further content verification remains for another probe.",
        ["blockerKind"] = ""
    };

}
