namespace Ansight.Host.Tests.Unit.SimulatorAgent;

public sealed partial class SimulatorAgentServiceTests
{
    [Fact]
    public async Task RunAsyncLoadsKeyboardInputAndExecutesEnter()
    {
        var gateway = new FakeToolGateway();
        AddToolLoadingDefinitions(gateway, "ansight_find_ui", "ansight_tap_ui", "ansight_press_keyboard");
        var session = new FakeOpenAiSession([
            CreateFunctionTurn("load", "ansight_load_tools", new JsonObject { ["bundle"] = "keyboard" }),
            CreateFunctionTurn("enter", "ansight_press_keyboard", new JsonObject { ["key"] = "Enter" }),
            CompleteWebSocketTurn()
        ]);
        using var service = CreateToolLoadingService(session, gateway);

        var result = await service.RunAsync(ToolLoadingRequest("Press Enter to activate the default button."));

        Assert.Equal(SimulatorAgentRunStatus.Succeeded, result.Status);
        Assert.DoesNotContain("ansight_press_keyboard", ToolNames(session.Requests[0].Tools));
        Assert.Contains(AdditionalToolItems(session.Requests[1].Input), item =>
            ToolNames(item["tools"]!.AsArray()).Contains("ansight_press_keyboard"));
        var call = Assert.Single(gateway.Calls);
        Assert.Equal("ansight_press_keyboard", call.ToolName);
        Assert.Equal("Enter", call.Arguments["key"]!.GetValue<string>());
    }
}
