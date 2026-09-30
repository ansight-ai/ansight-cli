using Ansight.SimCtl;

namespace Ansight.Host.Tests.Unit.NativeLogs;

public sealed class SimCtlDevicePowerTests
{
    private const string DeviceUdid = "AC42C25B-229C-4EAC-B7F7-3BC888988022";

    [Theory]
    [InlineData(true, "boot")]
    [InlineData(false, "shutdown")]
    public async Task DevicePowerAction_InvokesExpectedSimCtlCommand(bool shouldBoot, string expectedAction)
    {
        var commandRunner = new RecordingCommandRunner(
            new SimCtlCommandResult(0, string.Empty, string.Empty));
        var client = new SimCtlClient(CreateResolution(), commandRunner);

        if (shouldBoot)
        {
            await client.BootAsync(DeviceUdid);
        }
        else
        {
            await client.ShutdownAsync(DeviceUdid);
        }

        Assert.Equal(["simctl", expectedAction, DeviceUdid], commandRunner.Arguments);
    }

    [Fact]
    public async Task BootAsync_ReportsSimCtlFailure()
    {
        var commandRunner = new RecordingCommandRunner(
            new SimCtlCommandResult(2, string.Empty, "Unable to boot device."));
        var client = new SimCtlClient(CreateResolution(), commandRunner);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.BootAsync(DeviceUdid));

        Assert.Contains("start the simulator", exception.Message);
        Assert.Contains("Unable to boot device.", exception.Message);
    }

    [Fact]
    public async Task WaitForBootAsync_BlocksUntilSimCtlReportsReady()
    {
        var commandRunner = new RecordingCommandRunner(
            new SimCtlCommandResult(0, string.Empty, string.Empty));
        var client = new SimCtlClient(CreateResolution(), commandRunner);

        await client.WaitForBootAsync(DeviceUdid);

        Assert.Equal(
            ["simctl", "bootstatus", DeviceUdid, "-b"],
            commandRunner.Arguments);
    }

    [Fact]
    public async Task WaitForBootAsync_ReportsSimCtlFailure()
    {
        var commandRunner = new RecordingCommandRunner(
            new SimCtlCommandResult(2, string.Empty, "Boot status failed."));
        var client = new SimCtlClient(CreateResolution(), commandRunner);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.WaitForBootAsync(DeviceUdid));

        Assert.Contains("finish booting", exception.Message);
        Assert.Contains("Boot status failed.", exception.Message);
    }

    [Fact]
    public async Task SetLocationAsync_InvokesSimCtlWithInvariantCoordinates()
    {
        var commandRunner = new RecordingCommandRunner(
            new SimCtlCommandResult(0, string.Empty, string.Empty));
        var client = new SimCtlClient(CreateResolution(), commandRunner);

        await client.SetLocationAsync(DeviceUdid, -33.8688d, 151.2093d);

        Assert.Equal(
            ["simctl", "location", DeviceUdid, "set", "-33.8688,151.2093"],
            commandRunner.Arguments);
    }

    [Fact]
    public async Task ClearLocationAsync_InvokesSimCtlClear()
    {
        var commandRunner = new RecordingCommandRunner(
            new SimCtlCommandResult(0, string.Empty, string.Empty));
        var client = new SimCtlClient(CreateResolution(), commandRunner);

        await client.ClearLocationAsync(DeviceUdid);

        Assert.Equal(
            ["simctl", "location", DeviceUdid, "clear"],
            commandRunner.Arguments);
    }

    [Theory]
    [InlineData(true, "launch")]
    [InlineData(false, "terminate")]
    public async Task ApplicationLifecycleAction_InvokesExpectedSimCtlCommand(
        bool shouldLaunch,
        string expectedAction)
    {
        var commandRunner = new RecordingCommandRunner(
            new SimCtlCommandResult(0, string.Empty, string.Empty));
        var client = new SimCtlClient(CreateResolution(), commandRunner);

        if (shouldLaunch)
        {
            await client.LaunchApplicationAsync(DeviceUdid, "com.example.app");
        }
        else
        {
            await client.TerminateApplicationAsync(DeviceUdid, "com.example.app");
        }

        Assert.Equal(
            ["simctl", expectedAction, DeviceUdid, "com.example.app"],
            commandRunner.Arguments);
    }

    [Fact]
    public async Task TerminateApplicationAsync_WhenApplicationIsNotRunning_Succeeds()
    {
        var commandRunner = new RecordingCommandRunner(
            new SimCtlCommandResult(
                3,
                string.Empty,
                "Application com.example.app is not running."));
        var client = new SimCtlClient(CreateResolution(), commandRunner);

        var exception = await Record.ExceptionAsync(
            () => client.TerminateApplicationAsync(DeviceUdid, "com.example.app"));

        Assert.Null(exception);
        Assert.Equal(
            ["simctl", "terminate", DeviceUdid, "com.example.app"],
            commandRunner.Arguments);
    }

    [Fact]
    public async Task InstallApplicationAsync_InvokesSimCtlInstallWithAppBundle()
    {
        var appBundlePath = Path.Combine(
            Path.GetTempPath(),
            $"ansight-simctl-install-{Guid.NewGuid():N}.app");
        Directory.CreateDirectory(appBundlePath);
        try
        {
            var commandRunner = new RecordingCommandRunner(
                new SimCtlCommandResult(0, string.Empty, string.Empty));
            var client = new SimCtlClient(CreateResolution(), commandRunner);

            await client.InstallApplicationAsync(DeviceUdid, appBundlePath);

            Assert.Equal(
                ["simctl", "install", DeviceUdid, appBundlePath],
                commandRunner.Arguments);
        }
        finally
        {
            Directory.Delete(appBundlePath, recursive: true);
        }
    }

    private static SimCtlToolResolution CreateResolution()
        => SimCtlToolResolution.Found(
            "/Applications/Xcode.app/Contents/Developer",
            "/usr/bin/xcrun",
            "/Applications/Xcode.app/Contents/Developer/usr/bin/simctl",
            "test");

    private sealed class RecordingCommandRunner : ISimCtlCommandRunner
    {
        private readonly SimCtlCommandResult result;

        public RecordingCommandRunner(SimCtlCommandResult result)
        {
            this.result = result;
        }

        public IReadOnlyList<string> Arguments { get; private set; } = [];

        public Task<SimCtlCommandResult> RunAsync(
            SimCtlToolResolution toolResolution,
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken = default)
        {
            Arguments = arguments.ToArray();
            return Task.FromResult(result);
        }
    }
}
