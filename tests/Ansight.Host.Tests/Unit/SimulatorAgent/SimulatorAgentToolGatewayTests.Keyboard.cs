namespace Ansight.Host.Tests.Unit.SimulatorAgent;

public sealed partial class SimulatorAgentToolGatewayTests
{
    [Theory]
    [InlineData("ansight_open_keyboard")]
    [InlineData("ansight_dismiss_keyboard")]
    [InlineData("ansight_press_keyboard")]
    [InlineData("ansight_submit_keyboard")]
    public async Task KeyboardActionsAreExposedAndBoundToTheSelectedSession(string toolName)
    {
        var dispatcher = new RecordingOperationDispatcher(
            ToolDefinition(toolName, "Keyboard action", "sessionId", "appId", "deviceId", "key"));
        var gateway = new ToolGateway(dispatcher);
        var definition = Assert.Single(gateway.BuildOpenAiToolDefinitions().OfType<JsonObject>());
        var properties = definition["parameters"]!["properties"]!.AsObject();

        Assert.Equal(toolName, definition["name"]!.GetValue<string>());
        Assert.DoesNotContain("sessionId", properties);
        Assert.DoesNotContain("appId", properties);
        Assert.DoesNotContain("deviceId", properties);

        await gateway.ExecuteAsync(toolName, new JsonObject
        {
            ["sessionId"] = "wrong-session",
            ["deviceId"] = "wrong-device",
            ["key"] = "Enter",
            ["includeScreenshot"] = true
        }, "selected-session", "keyboard-call", CancellationToken.None);

        var call = Assert.Single(dispatcher.Calls);
        Assert.Equal(toolName, call.ToolName);
        Assert.Equal("selected-session", call.Arguments!["sessionId"]!.GetValue<string>());
        Assert.Null(call.Arguments["deviceId"]);
        Assert.Equal("Enter", call.Arguments["key"]!.GetValue<string>());
        Assert.False(call.Arguments["includeScreenshot"]!.GetValue<bool>());
    }

    [Fact]
    public void KeyboardVisibilityIsAvailableAsAReadOnlyObservation()
    {
        var dispatcher = new RecordingOperationDispatcher(
            ToolDefinition("ansight_is_keyboard_open", "Observe keyboard", "sessionId"));
        var definition = Assert.Single(new ToolGateway(dispatcher).BuildOpenAiToolDefinitions().OfType<JsonObject>());

        Assert.Equal("ansight_is_keyboard_open", definition["name"]!.GetValue<string>());
    }
}
