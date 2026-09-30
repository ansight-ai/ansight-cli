using Ansight.SimCtl;
using Ansight.SimCtl.Processes;

namespace Ansight.Host.Tests.Unit.NativeLogs;

public sealed class SimCtlProcessLauncherTests
{
    private const string DeviceUdid = "AC42C25B-229C-4EAC-B7F7-3BC888988022";

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    public async Task RealPipeCleanup_AfterCallerClosesStdinPreservesExitCode(int exitCode)
    {
        if (OperatingSystem.IsWindows()) return; // SimCtl's production launcher is a Unix adapter.
        await using var process = new DotNetProcessLauncher().Start(new(
            "/bin/sh", CreateResolution().DeveloperDirectory, ["-c", $"exit {exitCode}"]));
        process.StandardInput.Dispose();
        Assert.Equal(exitCode, await process.Completion.WaitAsync(TimeSpan.FromSeconds(5)));
        await process.DisposeAsync();
        await process.DisposeAsync();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    public async Task ShowSimulatorAsync_RealPipeCleanupDoesNotMaskLaunchResult(int exitCode)
    {
        if (OperatingSystem.IsWindows()) return;
        var launcher = new ShellProcessLauncher($"printf 'window-test-error' >&2; exit {exitCode}");
        var client = new SimCtlClient(CreateResolution(), new UnusedCommandRunner(), launcher);
        if (exitCode == 0)
        {
            await client.ShowSimulatorAsync(DeviceUdid);
        }
        else
        {
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => client.ShowSimulatorAsync(DeviceUdid));
            Assert.Equal("Simulator.app could not be shown: window-test-error", exception.Message);
        }
    }

    private sealed class ShellProcessLauncher(string command) : ISimCtlProcessLauncher
    {
        public ISimCtlProcess Start(SimCtlProcessStartRequest request)
        {
            Assert.Equal("/usr/bin/open", request.ExecutablePath);
            Assert.Equal(DeviceUdid, request.Arguments.Last());
            // Exercise real OS pipes and the actual adapter, without opening GUI apps in tests.
            return new DotNetProcessLauncher().Start(new(
                "/bin/sh", request.DeveloperDirectory, ["-c", command]));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealPipeCleanup_TerminatesRunningChildWithOpenOrClosedStdin(bool closeStdin)
    {
        if (OperatingSystem.IsWindows()) return;
        await using var process = new DotNetProcessLauncher().Start(new(
            "/bin/sh", CreateResolution().DeveloperDirectory, ["-c", "printf ready; exec /bin/sleep 30"]));
        // Retain the exact test-owned process handle so a regression cannot leak it.
        using var child = System.Diagnostics.Process.GetProcessById(process.ProcessId);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await process.StandardOutput.ReadExactlyAsync(new byte[5], timeout.Token);
            Assert.False(process.Completion.IsCompleted);
            if (closeStdin) process.StandardInput.Dispose();
            await process.DisposeAsync().AsTask().WaitAsync(timeout.Token);
            Assert.True(process.Completion.IsCompletedSuccessfully);
            Assert.True(child.HasExited);
            await process.DisposeAsync();
        }
        finally
        {
            if (!child.HasExited) child.Kill(entireProcessTree: true);
        }
    }

    [Fact]
    public async Task StartLogStreamAsync_UsesInjectedProcessLauncher()
    {
        var processLauncher = new RecordingProcessLauncher();
        var client = new SimCtlClient(
            CreateResolution(),
            new UnusedCommandRunner(),
            processLauncher);

        await using var logStream = await client.StartLogStreamAsync(
            new SimCtlLogStreamRequest(DeviceUdid, 4321, "debug"));
        await logStream.Completion;

        Assert.Equal("/usr/bin/xcrun", processLauncher.LastRequest?.ExecutablePath);
        Assert.Equal(
            [
                "simctl", "spawn", DeviceUdid, "log", "stream",
                "--style", "ndjson", "--color", "none", "--level", "debug",
                "--process", "4321", "--type", "log"
            ],
            processLauncher.LastRequest?.Arguments);
        Assert.True(processLauncher.Process?.StandardInputWasDisposed);
    }

    [Fact]
    public async Task ShowSimulatorAsync_OpensTheSelectedDeviceInSimulatorApp()
    {
        var processLauncher = new RecordingProcessLauncher();
        var client = new SimCtlClient(
            CreateResolution(),
            new UnusedCommandRunner(),
            processLauncher);

        await client.ShowSimulatorAsync(DeviceUdid);

        Assert.Equal("/usr/bin/open", processLauncher.LastRequest?.ExecutablePath);
        Assert.Equal(
            "/Applications/Xcode.app/Contents/Developer",
            processLauncher.LastRequest?.DeveloperDirectory);
        Assert.Equal(
            [
                "-a",
                Path.Combine(
                    "/Applications/Xcode.app/Contents/Developer",
                    "Applications",
                    "Simulator.app"),
                "--args",
                "-CurrentDeviceUDID",
                DeviceUdid
            ],
            processLauncher.LastRequest?.Arguments);
        Assert.True(processLauncher.Process?.StandardInputWasDisposed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StartSimulatorAsync_AlwaysBootsButOnlyOpensWindowWhenVisible(bool headless)
    {
        var processLauncher = new RecordingProcessLauncher();
        var commandRunner = new RecordingCommandRunner();
        var client = new SimCtlClient(CreateResolution(), commandRunner, processLauncher);

        await DeviceService.StartSimulatorAsync(client, DeviceUdid, headless, CancellationToken.None);

        Assert.Equal(
            [$"simctl boot {DeviceUdid}", $"simctl bootstatus {DeviceUdid} -b"],
            commandRunner.Commands);
        if (headless)
        {
            Assert.Null(processLauncher.LastRequest);
        }
        else
        {
            Assert.Equal("/usr/bin/open", processLauncher.LastRequest?.ExecutablePath);
            Assert.Equal(DeviceUdid, processLauncher.LastRequest?.Arguments.Last());
        }
    }

    [Theory]
    [InlineData("boot")]
    [InlineData("bootstatus")]
    public async Task StartSimulatorAsync_DoesNotOpenWindowWhenBootFails(string failedCommand)
    {
        var processLauncher = new RecordingProcessLauncher();
        var client = new SimCtlClient(
            CreateResolution(), new RecordingCommandRunner(failedCommand), processLauncher);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            DeviceService.StartSimulatorAsync(client, DeviceUdid, headless: false, CancellationToken.None));

        Assert.Null(processLauncher.LastRequest);
    }

    private sealed class RecordingCommandRunner(string? failedCommand = null) : ISimCtlCommandRunner
    {
        public List<string> Commands { get; } = [];

        public Task<SimCtlCommandResult> RunAsync(
            SimCtlToolResolution toolResolution,
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken = default)
        {
            Commands.Add(string.Join(" ", arguments));
            return Task.FromResult(new SimCtlCommandResult(
                arguments[1] == failedCommand ? 1 : 0, string.Empty, string.Empty));
        }
    }

    private static SimCtlToolResolution CreateResolution()
        => SimCtlToolResolution.Found(
            "/Applications/Xcode.app/Contents/Developer",
            "/usr/bin/xcrun",
            "/Applications/Xcode.app/Contents/Developer/usr/bin/simctl",
            "test");

    private sealed class RecordingProcessLauncher : ISimCtlProcessLauncher
    {
        public SimCtlProcessStartRequest? LastRequest { get; private set; }

        public RecordingProcess? Process { get; private set; }

        public ISimCtlProcess Start(SimCtlProcessStartRequest request)
        {
            LastRequest = request;
            Process = new RecordingProcess();
            return Process;
        }
    }

    private sealed class RecordingProcess : ISimCtlProcess
    {
        private readonly TrackingMemoryStream standardInput = new();

        public int ProcessId => 1234;

        public Stream StandardInput => standardInput;

        public Stream StandardOutput { get; } = new MemoryStream();

        public Stream StandardError { get; } = new MemoryStream();

        public Task<int> Completion { get; } = Task.FromResult(0);

        public bool StandardInputWasDisposed => standardInput.WasDisposed;

        public void Terminate(bool force = false)
        {
        }

        public ValueTask DisposeAsync()
        {
            standardInput.Dispose();
            StandardOutput.Dispose();
            StandardError.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TrackingMemoryStream : MemoryStream
    {
        public bool WasDisposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            WasDisposed = true;
            base.Dispose(disposing);
        }
    }

    private sealed class UnusedCommandRunner : ISimCtlCommandRunner
    {
        public Task<SimCtlCommandResult> RunAsync(
            SimCtlToolResolution toolResolution,
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("The command runner should not be used by this test.");
    }
}
