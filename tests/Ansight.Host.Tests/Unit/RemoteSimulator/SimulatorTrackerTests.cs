using Ansight.RemoteSimulator.Core.Simulator.Apple;
using Ansight.SimCtl;

namespace Ansight.Host.Tests.Unit.RemoteSimulator;

public sealed class SimulatorTrackerTests
{
    [Fact]
    public async Task StartAsync_DoesNotKeepEnumeratingAnIdleHost()
    {
        var runner = new DeviceListRunner();
        await using var tracker = new SimulatorTracker(CreateClient(runner), TimeSpan.FromMilliseconds(10));

        await tracker.StartAsync();
        await Task.Delay(80);

        Assert.Equal(1, runner.CallCount);
        Assert.Equal(["simctl", "list", "--json", "devices"], runner.LastArguments);
    }

    [Fact]
    public async Task RefreshIfStaleAsync_CoalescesRequestsAndForceRefreshFindsNewDevice()
    {
        var runner = new DeviceListRunner();
        await using var tracker = new SimulatorTracker(CreateClient(runner), TimeSpan.FromMinutes(1));
        await tracker.StartAsync();

        runner.DeviceState = "Booted";
        await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => tracker.RefreshIfStaleAsync()));

        Assert.Equal(1, runner.CallCount);
        Assert.False(Assert.Single(tracker.Current.Devices).IsBooted);

        await tracker.RefreshAsync();

        Assert.Equal(2, runner.CallCount);
        Assert.True(Assert.Single(tracker.Current.Devices).IsBooted);
    }

    [Fact]
    public async Task RefreshAsync_DoesNotUseStaleDevicesAfterDiscoveryFailure()
    {
        var runner = new DeviceListRunner { DeviceState = "Booted" };
        await using var tracker = new SimulatorTracker(CreateClient(runner));
        await tracker.StartAsync();
        Assert.True(Assert.Single(tracker.Current.Devices).IsBooted);

        runner.Fail = true;
        var snapshot = await tracker.RefreshAsync();

        Assert.Empty(snapshot.Devices);
        Assert.NotNull(snapshot.Error);
    }

    private static SimCtlClient CreateClient(ISimCtlCommandRunner runner)
        => new(SimCtlToolResolution.Found(
            "/Applications/Xcode.app/Contents/Developer",
            "/usr/bin/xcrun",
            "/Applications/Xcode.app/Contents/Developer/usr/bin/simctl",
            "test"), runner);

    private sealed class DeviceListRunner : ISimCtlCommandRunner
    {
        private int callCount;

        public int CallCount => Volatile.Read(ref callCount);

        public string DeviceState { get; set; } = "Shutdown";

        public bool Fail { get; set; }

        public IReadOnlyList<string> LastArguments { get; private set; } = [];

        public Task<SimCtlCommandResult> RunAsync(
            SimCtlToolResolution toolResolution,
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken = default)
        {
            LastArguments = arguments.ToArray();
            Interlocked.Increment(ref callCount);
            if (Fail)
            {
                return Task.FromResult(new SimCtlCommandResult(1, string.Empty, "simctl failed"));
            }
            return Task.FromResult(new SimCtlCommandResult(0, $$"""
                {
                  "devices": {
                    "com.apple.CoreSimulator.SimRuntime.iOS-26-4": [
                      {
                        "udid": "C868C8C0-F2DE-4C66-A337-142401E11B35",
                        "isAvailable": true,
                        "state": "{{DeviceState}}",
                        "name": "iPhone 17"
                      }
                    ]
                  }
                }
                """, string.Empty));
        }
    }
}
