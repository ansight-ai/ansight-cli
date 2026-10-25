namespace Ansight.Host.Tests.Unit.SimulatorAgent;

public sealed partial class SimulatorAgentServiceTests
{
    [Fact]
    public async Task RunAsyncLoadsAndExecutesLongPressThroughTheGestureBundle()
    {
        var gateway = new FakeToolGateway();
        AddToolLoadingDefinitions(gateway, "ansight_find_ui", "ansight_tap_ui", "ansight_long_press_ui");
        var session = new FakeOpenAiSession([
            CreateFunctionTurn("load", "ansight_load_tools", new JsonObject { ["bundle"] = "gestures" }),
            CreateFunctionTurn("hold", "ansight_long_press_ui", new JsonObject
            {
                ["automationId"] = "card", ["durationMs"] = 1800
            }),
            CompleteWebSocketTurn()
        ]);
        using var service = CreateToolLoadingService(session, gateway);
        var result = await service.RunAsync(ToolLoadingRequest("Long press the card."));
        Assert.Equal(SimulatorAgentRunStatus.Succeeded, result.Status);
        Assert.DoesNotContain("ansight_long_press_ui", ToolNames(session.Requests[0].Tools));
        Assert.Contains(AdditionalToolItems(session.Requests[1].Input), item =>
            ToolNames(item["tools"]!.AsArray()).Contains("ansight_long_press_ui"));
        var call = Assert.Single(gateway.Calls);
        Assert.Equal("ansight_long_press_ui", call.ToolName);
        Assert.Equal(1800, call.Arguments["durationMs"]!.GetValue<int>());
        Assert.Contains("ansight_long_press_ui", AgentToolCatalog.ReadGuidance("gestures"));
    }
}
