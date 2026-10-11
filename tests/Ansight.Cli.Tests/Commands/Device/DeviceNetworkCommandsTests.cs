using System.Text.Json.Nodes;
using Ansight.Cli.Commands.Device;
using Ansight.Host.Devices.Network;

namespace Ansight.Cli.Tests.Commands.Device;

public sealed class DeviceNetworkCommandsTests
{
    [Fact]
    public async Task SetForwardsProfileAndLeaseToResidentHost()
    {
        var arguments = CliArguments.Parse(["device", "network", "set", "--session", "test", "--lease", "owned",
            "--profile", "{\"downloadKbps\":128}", "--duration-ms", "30000", "--json"]);
        Assert.True(ControlClient.ShouldForward(arguments));
        Assert.True(ControlProtocol.SupportsCooperativeCancellation(arguments));
        var exit = await DeviceNetworkCommands.RunAsync(arguments, new CliOutput(true, TextWriter.Null, TextWriter.Null),
            (action, value, _) =>
            {
                Assert.Equal("set", action);
                Assert.Equal("owned", value["leaseId"]!.GetValue<string>());
                Assert.Equal("test", value["sessionId"]!.GetValue<string>());
                Assert.Equal(128, value["profile"]!["downloadKbps"]!.GetValue<int>());
                return Task.FromResult(new NetworkConditioningResult(true, new JsonObject { ["status"] = "ready" }));
            }, default);
        Assert.Equal(CliExitCodes.Success, exit);
    }

    [Fact]
    public async Task ResetCanRunAfterSessionDisconnect()
    {
        var arguments = CliArguments.Parse(["device", "network", "reset", "--lease", "owned"]);
        Assert.True(ControlClient.ShouldForward(arguments));
        await DeviceNetworkCommands.RunAsync(arguments, new CliOutput(false, TextWriter.Null, TextWriter.Null),
            (_, value, _) =>
            {
                Assert.Null(value["sessionId"]);
                Assert.Equal("owned", value["leaseId"]!.GetValue<string>());
                return Task.FromResult(new NetworkConditioningResult(true, new JsonObject { ["status"] = "stopped" }));
            }, default);
    }
}
