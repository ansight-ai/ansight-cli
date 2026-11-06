using System.Text;
using Ansight.Host.Runtime.DeviceExecution;
using Ansight.RemoteSimulator.Core.Devices;

namespace Ansight.Host.Tests.Unit.Devices;

public sealed class CompanionAppTestingServiceTests
{
    [Theory]
    [InlineData("app-resume", "launch")]
    [InlineData("app-restart", "launch")]
    [InlineData("display-appearance", "ui")]
    [InlineData("display-text-size", "ui")]
    public async Task CommandsArePinnedToTheSelectedSimulatorAndApp(string action, string command)
    {
        var runner = new FakeCommands();
        var service = Create(runner);
        var value = action == "display-text-size" ? "accessibility-large" : "dark";
        var result = await service.ExecuteAsync(new RemoteDeviceTestingRequest(
            "selected-sim", action, "com.example.app", Value: value), default);

        Assert.True(result.IsSuccess, result.Message);
        var call = runner.Calls.Single(arguments => arguments[0] == command);
        Assert.Contains("selected-sim", call);
        Assert.DoesNotContain("booted", call);
        if (action.StartsWith("app-", StringComparison.Ordinal))
        {
            Assert.Equal("com.example.app", call[^1]);
            Assert.Equal(action == "app-restart", call.Contains("--terminate-running-process"));
        }
    }

    [Theory]
    [InlineData("permission-reset", "all", null)]
    [InlineData("display-appearance", null, "dark; shutdown all")]
    [InlineData("display-text-size", null, "giant")]
    [InlineData("display-rotation", null, "face-up")]
    [InlineData("open-link", null, "file:///etc/passwd")]
    [InlineData("open-link", null, "javascript:alert(1)")]
    public async Task InvalidValuesNeverReachMutationCommands(string action, string? permission, string? value)
    {
        var runner = new FakeCommands();
        var result = await Create(runner).ExecuteAsync(new RemoteDeviceTestingRequest(
            "selected-sim", action, "com.example.app", Permission: permission, Value: value), default);

        Assert.False(result.IsSuccess);
        Assert.All(runner.Calls, arguments => Assert.Equal("get_app_container", arguments[0]));
    }

    [Fact]
    public async Task MissingAppPreventsRestart()
    {
        var runner = new FakeCommands { Installed = false };
        var result = await Create(runner).ExecuteAsync(new RemoteDeviceTestingRequest(
            "selected-sim", "app-restart", "com.example.missing"), default);

        Assert.False(result.IsSuccess);
        Assert.Equal("get_app_container", Assert.Single(runner.Calls)[0]);
    }

    [Fact]
    public async Task PushUsesValidatedTemporaryJsonAndRemovesIt()
    {
        var runner = new FakeCommands();
        const string payload = "{\"aps\":{\"alert\":\"Hello\"},\"path\":\"detail/42\"}";
        var result = await Create(runner).ExecuteAsync(new RemoteDeviceTestingRequest(
            "selected-sim", "push", "com.example.app", Payload: payload), default);

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(payload, runner.PushPayload);
        var call = runner.Calls.Single(arguments => arguments[0] == "push");
        Assert.Equal("com.example.app", call[2]);
        Assert.False(File.Exists(call[3]));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{\"aps\":\"bad\"}")]
    [InlineData("{\"aps\":{},\"Simulator Target Bundle\":\"com.other.app\"}")]
    public void InvalidPushIsRejected(string payload)
    {
        Assert.Throws<ArgumentException>(() => CompanionAppTestingService.ValidatePush(payload, "com.example.app"));
    }

    [Fact]
    public void PushLimitCountsUtf8Bytes()
    {
        var payload = "{\"aps\":{\"alert\":\"" + new string('界', 1400) + "\"}}";
        Assert.Throws<ArgumentException>(() => CompanionAppTestingService.ValidatePush(payload, "com.example.app"));
    }

    [Fact]
    public async Task CapabilitiesOnlyExposeInstalledPrivacyServices()
    {
        var runner = new FakeCommands();
        var result = await Create(runner).ExecuteAsync(new RemoteDeviceTestingRequest(
            "selected-sim", "app-capabilities"), default);

        Assert.True(result.IsSuccess);
        Assert.Equal(new[] { "location", "microphone" }, result.Details!["permissions"]!.AsArray().Select(node => node!.GetValue<string>()));
        Assert.Equal("help", Assert.Single(runner.Calls)[0]);
    }

    [Theory]
    [InlineData("physical", true)]
    [InlineData("virtual", false)]
    public async Task PhysicalOrStoppedTargetsNeverRunCommands(string kind, bool booted)
    {
        var runner = new FakeCommands();
        var result = await Create(runner, kind, booted).ExecuteAsync(new RemoteDeviceTestingRequest(
            "selected-sim", "app-restart", "com.example.app"), default);

        Assert.False(result.IsSuccess);
        Assert.Empty(runner.Calls);
    }

    private static CompanionAppTestingService Create(FakeCommands commands, string kind = "virtual", bool booted = true)
    {
        return new CompanionAppTestingService(
            _ => Task.FromResult(new DeviceInventory([], [new DeviceDescriptor(
                "selected-sim", "Selected simulator", "ios", "iOS", "Booted", booted, true, kind)], [])),
            new RuntimeOptions(), commands, "/simctl");
    }

    private sealed class FakeCommands : IDeviceCommandRunner
    {
        public List<string[]> Calls { get; } = [];
        public bool Installed { get; init; } = true;
        public string? PushPayload { get; private set; }

        public Task<DeviceCommandResult> RunAsync(string executable, IReadOnlyList<string> arguments,
            CancellationToken cancellationToken, int maximumBytes = 1_048_576)
        {
            Calls.Add(arguments.ToArray());
            if (arguments[0] == "get_app_container" && !Installed)
            {
                return Task.FromResult(new DeviceCommandResult(1, [], "App is not installed."));
            }

            if (arguments[0] == "push")
            {
                PushPayload = File.ReadAllText(arguments[3]);
            }

            var output = arguments[0] == "help" ? "  microphone - Audio\n  location - Location\n  all - All" : "";
            return Task.FromResult(new DeviceCommandResult(0, Encoding.UTF8.GetBytes(output), ""));
        }
    }
}
