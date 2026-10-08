using System.Text.Json.Nodes;

namespace Ansight.Host.Tests.Unit.SimulatorAgent;

public sealed partial class SimulatorAgentServiceTests
{
    [Theory]
    [InlineData(true, true, "ansight_get_live_visual_tree", false, true)]
    [InlineData(false, true, "ansight_get_live_visual_tree", false, true)]
    [InlineData(false, false, "ansight_get_live_visual_tree", false, false)]
    [InlineData(false, true, "ansight_get_live_visual_tree", true, false)]
    [InlineData(false, true, "ansight_run_task", false, false)]
    public async Task RunAsync_ExplainsIncompleteObservationsWithoutTreatingThemAsAbsence(
        bool initialObservation, bool truncated, string toolName, bool isError, bool expectsRecovery)
    {
        var observation = new ToolCallResult(isError, new JsonObject
        {
            ["isError"] = isError,
            ["result"] = new JsonObject
            {
                ["capability"] = "ui.observe",
                ["truncated"] = truncated,
                ["root"] = new JsonObject { ["automationId"] = "ForYouPage" }
            }
        }.ToJsonString(), "Partial page observation.");
        var gateway = new FakeToolGateway
        {
            InitialObservation = initialObservation ? observation : null,
            Result = observation
        };
        AddToolLoadingDefinitions(gateway);
        var turns = initialObservation
            ? new[] { CompleteWebSocketTurn() }
            : new[] { CreateFunctionTurn("observe", toolName, new JsonObject()), CompleteWebSocketTurn() };
        var session = new FakeOpenAiSession(turns);
        using var service = CreateToolLoadingService(session, gateway);

        await service.RunAsync(ToolLoadingRequest("Inspect the current page."));

        var input = session.Requests[^1].Input.ToJsonString();
        Assert.Equal(expectsRecovery, input.Contains("This UI observation is truncated:", StringComparison.Ordinal));
        if (expectsRecovery)
        {
            Assert.Contains("not absent", input);
            Assert.Contains("native input values already observed", input);
            Assert.Contains("ansight_scan_screen", input);
            Assert.Contains("Avoid repeating a full-tree capture", input);
        }
    }

    [Fact]
    public async Task RunAsync_RetainsPositiveSearchEvidenceWhenLaterFrameworkTreeIsTruncated()
    {
        var gateway = new FakeToolGateway();
        gateway.Results.Enqueue(new ToolCallResult(false, "{\"performed\":true}", "Typed."));
        gateway.Results.Enqueue(new ToolCallResult(false, """
            {"result":{"totalMatches":2,"matches":[
              {"text":"Eagle Rock","role":"text","onScreen":true,"tapHint":null},
              {"text":"Eagle Rock","role":"text","onScreen":true,"tapHint":null}]}}
            """, "Two visible results."));
        gateway.Results.Enqueue(new ToolCallResult(false, """
            {"result":{"capability":"ui.observe","truncated":true,"toolId":"maui.get_visual_tree",
              "root":{"automationId":"ForYouPage","children":[
                {"automationId":"for-you-global-search","text":"Search crags, routes or weather","role":"textbox"}]}}}
            """, "Truncated framework tree."));
        var client = new FakeOpenAiClient([
            CreateFunctionTurn("type", "ansight_type_text", new JsonObject
            {
                ["automationId"] = "for-you-global-search", ["value"] = "Eagle Rock"
            }),
            CreateFunctionTurn("find", "ansight_find_ui", new JsonObject { ["text"] = "Eagle Rock" }),
            CreateFunctionTurn("tree", "ansight_get_live_visual_tree", new JsonObject { ["toolId"] = "maui.get_visual_tree" }),
            CreateFunctionTurn("complete", "complete_instruction", new JsonObject
            {
                ["outcome"] = "failed", ["summary"] = "Result identity remains ambiguous."
            })
        ]);
        using var service = new SimulatorAgentService(new InMemoryEncryptedStorage(), client, gateway);
        service.SetDefaultModelAccessTokenForTesting("sk-local-test");

        var result = await service.RunAsync(new SimulatorAgentRunRequest("session-123",
            ["Search for and select Eagle Rock on the For you tab."]));

        var input = client.Inputs[^1].ToJsonString();
        Assert.Contains("Two visible results", result.Audit.ToolCalls[1].Message);
        Assert.Contains("surrounding result content or the screenshot", input);
        Assert.Contains("do not guess a list index", input);
        Assert.Contains("This UI observation is truncated:", input);
        Assert.Contains("A truncated tree cannot disprove a positive match", client.Requests[0].Instructions);
        Assert.Contains("do not switch tabs merely to satisfy that task", client.Requests[0].Instructions);
        // Recovery advice never changes a reported failure into success.
        Assert.Equal(SimulatorAgentRunStatus.Failed, result.Status);
    }
}
