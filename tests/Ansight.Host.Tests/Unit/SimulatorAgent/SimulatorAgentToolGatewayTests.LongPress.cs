namespace Ansight.Host.Tests.Unit.SimulatorAgent;

public sealed partial class SimulatorAgentToolGatewayTests
{
    [Fact]
    public async Task LongPressIsAllowedAndPreservesDurationBeyondTheSwipeLimit()
    {
        var dispatcher = new RecordingOperationDispatcher();
        var gateway = new ToolGateway(dispatcher);
        await gateway.ExecuteAsync("ansight_long_press_ui", new JsonObject
        {
            ["automationId"] = "card", ["durationMs"] = 3500, ["includeScreenshot"] = true,
            ["sessionId"] = "wrong"
        }, "hold-session", "hold-call", CancellationToken.None);
        var call = Assert.Single(dispatcher.Calls);
        Assert.Equal("ansight_long_press_ui", call.ToolName);
        Assert.Equal("hold-session", call.Arguments!["sessionId"]!.GetValue<string>());
        Assert.Equal(3500, call.Arguments["durationMs"]!.GetValue<int>());
        Assert.False(call.Arguments["includeScreenshot"]!.GetValue<bool>());
    }
}
