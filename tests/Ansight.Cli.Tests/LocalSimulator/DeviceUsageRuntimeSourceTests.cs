using Ansight.Cli.LocalSimulator;
using Ansight.Host.Devices;
using Ansight.RemoteSimulator.Core.Input;
using Ansight.RemoteSimulator.Core.Runtime;

namespace Ansight.Cli.Tests.LocalSimulator;

public sealed class DeviceUsageRuntimeSourceTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "ansight-usage-runtime-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void InventoryRefreshDoesNotCountAsUsageAndAndroidIdentitySurvivesSerialChanges()
    {
        var device = new RemoteRuntimeDevice("emulator-5554", "Pixel 9", "Booted", true, "API 35", "android", BootIdentifier: "Pixel_9");
        var source = new Source(device);
        var store = new DeviceUsageStore(directory);
        var runtime = new CliDeviceUsageRuntimeSource(source, store);

        Assert.Null(Assert.Single(runtime.Current.Devices).LastUsedUtc);
        runtime.RecordUse(device);
        var lastUsed = Assert.Single(runtime.Current.Devices).LastUsedUtc;
        Assert.NotNull(lastUsed);

        var reconnected = new CliDeviceUsageRuntimeSource(new Source(device with { Identifier = "emulator-5556" }), new DeviceUsageStore(directory));
        Assert.Equal(lastUsed, Assert.Single(reconnected.Current.Devices).LastUsedUtc);
        Assert.Null(store.Read("android", "emulator-5554").LastUsedUtc);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RemoteInputRecordsOnlySuccessfulDelivery(bool succeeds)
    {
        var device = new RemoteRuntimeDevice("sim", "iPhone", "Booted", true, "iOS", "ios");
        var store = new DeviceUsageStore(directory);
        var runtime = new CliDeviceUsageRuntimeSource(new Source(device), store);
        await using var router = new RoutedSimulatorInputSink(runtime,
            new Dictionary<string, ISimulatorInputSink> { ["ios"] = new Sink(succeeds) }, runtime.RecordUse);

        var result = await router.SendButtonAsync(new RemoteButtonEvent("sim", "home"));

        Assert.Equal(succeeds, result.IsSuccess);
        Assert.Equal(succeeds, store.Read("ios", "sim").LastUsedUtc.HasValue);
    }

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class Source : IRemoteRuntimeSource
    {
        public Source(RemoteRuntimeDevice device)
        {
            Current = new RemoteRuntimeSnapshot(DateTimeOffset.UtcNow, [device], null);
        }

        public RemoteRuntimeSnapshot Current { get; }
    }

    private sealed class Sink : ISimulatorInputSink
    {
        private readonly bool succeeds;

        public Sink(bool succeeds)
        {
            this.succeeds = succeeds;
        }

        public string BackendName => "test";
        public string Status => "ready";
        public Task<InputDeliveryResult> SendAsync(RemotePointerEvent pointerEvent, CancellationToken cancellationToken = default)
            => Task.FromResult(new InputDeliveryResult(succeeds, BackendName, "test"));
        public Task<InputDeliveryResult> SendButtonAsync(RemoteButtonEvent buttonEvent, CancellationToken cancellationToken = default)
            => Task.FromResult(new InputDeliveryResult(succeeds, BackendName, "test"));
        public Task<InputDeliveryResult> SendKeyAsync(RemoteKeyEvent keyEvent, CancellationToken cancellationToken = default)
            => Task.FromResult(new InputDeliveryResult(succeeds, BackendName, "test"));
        public Task<InputDeliveryResult> SendTextAsync(RemoteTextEvent textEvent, CancellationToken cancellationToken = default)
            => Task.FromResult(new InputDeliveryResult(succeeds, BackendName, "test"));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
