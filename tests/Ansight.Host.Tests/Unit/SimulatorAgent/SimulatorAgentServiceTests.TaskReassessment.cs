using System.Text.Json.Nodes;

namespace Ansight.Host.Tests.Unit.SimulatorAgent;

public sealed partial class SimulatorAgentServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunAsync_AllowsContentScrollingWithinDeclaredStepUntilNavigation(bool taskFailed)
    {
        const string taskId = "load-area-weather";
        const string taskToolName = "ansight_task_1_load_area_weather";
        var turns = new List<OpenAiTurn>();
        var gateway = new FakeToolGateway
        {
            RepositoryTaskShortcuts =
            [CreateRepositoryTaskShortcut(taskToolName, taskId, "Load area weather")]
        };
        if (taskFailed)
        {
            turns.Add(CreateFunctionTurn("task", taskToolName, new JsonObject()));
            gateway.Results.Enqueue(new ToolCallResult(
                true, "{\"isError\":true,\"message\":\"Weather task timed out.\"}", "Weather task timed out."));
        }

        turns.Add(CreateFunctionTurn("declare", "ansight_declare_uncovered_step", new JsonObject
        {
            ["uncoveredStep"] = "Scroll to the offscreen control and open it.",
            ["reason"] = taskFailed ? "task-failed" : "starting-state-not-satisfied",
            ["relatedTaskId"] = taskId,
            ["evidence"] = taskFailed
                ? "The weather task timed out, and the target remains below the viewport."
                : "The weather task requires a different starting tab; this control is on the current tab.",
            ["consideredTaskIds"] = taskFailed ? new JsonArray() : new JsonArray(taskId)
        }));
        turns.Add(CreateFunctionTurn("scroll-first", "ansight_scroll_ui", new JsonObject { ["orientation"] = "down" }));
        turns.Add(CreateFunctionTurn("observe", "ansight_find_ui", new JsonObject { ["automationId"] = "offscreen-control" }));
        turns.Add(CreateFunctionTurn("scroll-second", "ansight_scroll_ui", new JsonObject { ["orientation"] = "down" }));
        turns.Add(CreateFunctionTurn("open", "ansight_tap_ui", new JsonObject { ["automationId"] = "offscreen-control" }));
        turns.Add(CreateFunctionTurn("blocked-next-page-tap", "ansight_tap_ui", new JsonObject { ["automationId"] = "next-page-control" }));
        turns.Add(CreateFunctionTurn("complete", "complete_instruction", new JsonObject
        {
            ["outcome"] = "failed",
            ["summary"] = "The next page requires task reassessment."
        }));
        gateway.Results.Enqueue(PerformedResult());
        gateway.Results.Enqueue(new ToolCallResult(false,
            "{\"isError\":false,\"result\":{\"matches\":[{\"automationId\":\"offscreen-control\",\"onScreen\":false}]}}",
            "The target remains below the viewport."));
        gateway.Results.Enqueue(PerformedResult());
        gateway.Results.Enqueue(PerformedResult());
        using var service = new SimulatorAgentService(new InMemoryEncryptedStorage(), new FakeOpenAiClient(turns), gateway);
        service.SetDefaultModelAccessTokenForTesting("sk-local-test");

        var result = await service.RunAsync(new SimulatorAgentRunRequest(
            "session-123", ["Scroll to the offscreen control and open it, then continue on the next page."]));

        Assert.False(Assert.Single(result.Audit.ToolCalls, call => call.CallId == "declare").IsError);
        foreach (var callId in new[] { "scroll-first", "scroll-second", "open" })
        {
            Assert.False(Assert.Single(result.Audit.ToolCalls, call => call.CallId == callId).IsError);
        }
        var blocked = Assert.Single(result.Audit.ToolCalls, call => call.CallId == "blocked-next-page-tap");
        Assert.True(blocked.IsError);
        Assert.Contains("reassessment is required", blocked.Message, StringComparison.Ordinal);
        Assert.Equal(taskFailed ? 5 : 4, gateway.Calls.Count);

        static ToolCallResult PerformedResult()
            => new(false, "{\"isError\":false,\"result\":{\"performed\":true}}", "Gesture delivered.");
    }
}
