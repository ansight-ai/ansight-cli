using Ansight.Host.Devices;

namespace Ansight.Cli.Tests.Commands;

public sealed class HeadlessCommandsTests
{
    [Theory]
    [InlineData("device start ios DEVICE-1")]
    [InlineData("devices boot android Pixel")]
    [InlineData("app execute")]
    [InlineData("app-graph explore com.example.app")]
    [InlineData("test run /workspace test-id")]
    [InlineData("test run-all /workspace")]
    [InlineData("replay ansight session-1")]
    [InlineData("profile dotnet start /tmp/App.apk")]
    [InlineData("profile ios start /tmp/App.app")]
    [InlineData("profile android start /tmp/App.apk")]
    public void HeadlessIsBooleanBeforeOrAfterPositionals(string command)
    {
        var positionals = command.Split(' ');
        foreach (var arguments in new[]
                 {
                     CliArguments.Parse(["--headless", .. positionals]),
                     CliArguments.Parse([.. positionals, "--headless"])
                 })
        {
            Assert.True(arguments.HasFlag("headless"));
            Assert.Equal(positionals, arguments.Positionals);
            Assert.Null(arguments.GetOption("headless"));
        }
    }

    [Theory]
    [InlineData("app-graph explore com.example.app")]
    [InlineData("app execute")]
    [InlineData("test run /workspace test-id")]
    [InlineData("test run-all /workspace")]
    public void SharedLaunchTargetsPreserveHeadless(string command)
    {
        var target = WorkspaceTestTargetOptions.Resolve(
            CliArguments.Parse([.. command.Split(' '), "--headless"]));

        Assert.NotNull(target);
        Assert.True(target.Headless);
        Assert.Null(target.DeviceIdentifier);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExecuteAppliesWindowPreferenceToEveryTarget(bool headless)
    {
        string[] args =
        [
            "app", "execute", "--app-id", "com.example.app",
            "--device-id", "DEVICE-1", "--device-id", "DEVICE-2", "--prompt", "Open settings"
        ];
        var targets = AppExecutionService.ResolveTargets(
            CliArguments.Parse(headless ? [.. args, "--headless"] : args));

        Assert.Equal(2, targets.Count);
        Assert.All(targets, target =>
        {
            Assert.True(target.RequiresLaunch);
            Assert.NotNull(target.LaunchRequest);
            Assert.Equal(headless, target.LaunchRequest.Headless);
        });
    }

    [Theory]
    [InlineData("ansight")]
    [InlineData("sentry")]
    [InlineData("posthog")]
    public void ReplayAcceptsHeadlessWithoutOtherTargetOptions(string source)
    {
        var arguments = CliArguments.Parse(["--headless", "replay", source, "source-id"]);
        ReplayCommands.ValidateOptions(arguments);

        var target = ReplayCommands.ResolveReplayTargetRequest(arguments, sourceSnapshot: null);

        Assert.NotNull(target);
        Assert.True(target.Headless);
    }

    [Theory]
    [InlineData("device")]
    [InlineData("app")]
    [InlineData("app-graph")]
    [InlineData("replay")]
    [InlineData("test")]
    [InlineData("profile dotnet")]
    [InlineData("profile ios")]
    [InlineData("profile android")]
    [InlineData("task")]
    [InlineData("repo")]
    public async Task CommandHelpAdvertisesHeadlessAndRunsInIsolatedScope(string command)
    {
        using var output = new ContextRecordingWriter();
        using var error = new StringWriter();
        foreach (var headless in new[] { true, false })
        {
            output.Preferences.Clear();
            string[] args = [.. command.Split(' '), "--help"];
            var exitCode = await CliApplication.RunParsedAsync(
                CliArguments.Parse(headless ? [.. args, "--headless"] : args),
                new CliOutput(false, output, error),
                CancellationToken.None,
                allowResidentHostForwarding: false,
                accessAuthorizer: TestAccessAuthorizer.Allow);

            Assert.Equal(CliExitCodes.Success, exitCode);
            Assert.Contains("--headless", output.ToString(), StringComparison.Ordinal);
            Assert.NotEmpty(output.Preferences);
            Assert.All(output.Preferences, value => Assert.Equal(headless, value));
            Assert.False(DeviceLaunchContext.Headless);
        }
    }

    [Fact]
    public async Task ForwardingPreservesHeadlessFlag()
    {
        var forwarded = false;
        var exitCode = await CliApplication.RunParsedAsync(
            CliArguments.Parse(["--headless", "device", "boot", "ios", "DEVICE-1"]),
            new CliOutput(false, TextWriter.Null, TextWriter.Null),
            CancellationToken.None,
            allowResidentHostForwarding: true,
            accessAuthorizer: TestAccessAuthorizer.Allow,
            residentHostForwarder: (arguments, _, _) =>
            {
                var received = CliArguments.Parse(arguments.OriginalArguments);
                Assert.True(received.HasFlag("headless"));
                Assert.Equal(["device", "boot", "ios", "DEVICE-1"], received.Positionals);
                forwarded = true;
                return Task.FromResult<int?>(CliExitCodes.Success);
            });

        Assert.True(forwarded);
        Assert.Equal(CliExitCodes.Success, exitCode);
    }

    private sealed class ContextRecordingWriter : StringWriter
    {
        public List<bool> Preferences { get; } = [];

        public override void WriteLine(string? value)
        {
            Preferences.Add(DeviceLaunchContext.Headless);
            base.WriteLine(value);
        }
    }
}
