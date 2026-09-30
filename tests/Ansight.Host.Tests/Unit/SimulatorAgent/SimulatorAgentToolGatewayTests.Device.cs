using Ansight.Host.SimulatorAgent.Observations;

namespace Ansight.Host.Tests.Unit.SimulatorAgent;

public sealed partial class SimulatorAgentToolGatewayTests
{
    [Fact]
    public async Task DeviceModeExposesUiToolsAndRejectsSdkOperations()
    {
        var dispatcher = new RecordingOperationDispatcher(
            ToolDefinition("ansight_tap_ui", "Tap", "sessionId"),
            ToolDefinition("ansight_scan_screen", "Read screen", "sessionId"),
            ToolDefinition("ansight_run_task", "Task", "sessionId"),
            ToolDefinition("ansight_call_app_tool", "App tool", "sessionId"));
        dispatcher.SessionSnapshots.Add(CreateSessionSnapshot(
            "device-session", "com.example.app", DateTimeOffset.UtcNow, captureSource: "device"));
        var gateway = new ToolGateway(dispatcher);
        var capabilities = await gateway.GetSessionCapabilitiesAsync("device-session", CancellationToken.None);
        Assert.True(capabilities.IsDeviceOnly);
        var names = gateway.BuildOpenAiToolDefinitions(capabilities: capabilities)
            .OfType<JsonObject>().Select(value => value["name"]!.GetValue<string>()).ToArray();
        Assert.Contains("ansight_tap_ui", names);
        Assert.Contains("ansight_scan_screen", names);
        Assert.Contains("ansight_run_task", names);
        Assert.DoesNotContain("ansight_call_app_tool", names);
        Assert.Empty(await gateway.GetRepositoryTaskShortcutsAsync("device-session", "Inspect", CancellationToken.None));
        var discoveryCalls = dispatcher.Calls.Count;
        var result = await gateway.ExecuteAsync("ansight_call_app_tool", new JsonObject(),
            "device-session", "call", CancellationToken.None);
        Assert.True(result.IsError);
        Assert.Equal(discoveryCalls, dispatcher.Calls.Count);
    }
}
