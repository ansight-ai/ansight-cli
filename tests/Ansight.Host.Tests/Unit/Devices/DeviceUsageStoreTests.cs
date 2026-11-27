using Ansight.Host.Devices;

namespace Ansight.Host.Tests.Unit.Devices;

public sealed class DeviceUsageStoreTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "ansight-device-usage-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void BootObservationAndPollingDoNotCountAsUse()
    {
        var store = new DeviceUsageStore(directory);
        var boot = DateTimeOffset.UtcNow.AddHours(-1);
        store.Update("ios", "SIM-1", new DeviceUsageHistory(LastBootedUtc: boot));

        var restarted = new DeviceUsageStore(directory);
        var history = restarted.Read("ios", "sim-1");

        Assert.Equal(boot, history.LastBootedUtc);
        Assert.Null(history.LastUsedUtc);
        Assert.Equal(history, restarted.Read("ios", "SIM-1"));
        Assert.Null(store.Warning);
    }

    [Fact]
    public void UnknownDeviceReadDoesNotCreateHistory()
    {
        Assert.Equal(new DeviceUsageHistory(), new DeviceUsageStore(directory).Read("ios", "unknown"));
        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public void HistorySurvivesRestartAndOlderObservationsCannotOverwriteIt()
    {
        var boot = DateTimeOffset.UtcNow.AddHours(-2);
        var used = boot.AddHours(1);
        new DeviceUsageStore(directory).Update("ios", "sim", new DeviceUsageHistory(boot, used));
        var store = new DeviceUsageStore(directory);
        store.Update("ios", "sim", new DeviceUsageHistory(boot.AddDays(-1), used.AddDays(-1)));

        Assert.Equal(new DeviceUsageHistory(boot, used), store.Read("ios", "sim"));
        Assert.Null(store.Read("android", "sim").LastUsedUtc);
    }

    [Fact]
    public async Task ConcurrentWritersPreserveLatestBootAndUse()
    {
        var timestamp = DateTimeOffset.UtcNow;
        await Task.WhenAll(Enumerable.Range(0, 24).Select(index => Task.Run(() =>
        {
            var store = new DeviceUsageStore(directory);
            var observation = index % 2 == 0
                ? new DeviceUsageHistory(LastBootedUtc: timestamp.AddSeconds(index))
                : new DeviceUsageHistory(LastUsedUtc: timestamp.AddSeconds(index));
            store.Update("android", "Pixel_9", observation);
            Assert.Null(store.Warning);
        })));

        var history = new DeviceUsageStore(directory).Read("android", "Pixel_9");
        Assert.Equal(timestamp.AddSeconds(22), history.LastBootedUtc);
        Assert.Equal(timestamp.AddSeconds(23), history.LastUsedUtc);
    }

    [Fact]
    public void CorruptHistoryIsReportedAndNotOverwritten()
    {
        var store = new DeviceUsageStore(directory);
        store.Update("ios", "sim", new DeviceUsageHistory(LastUsedUtc: DateTimeOffset.UtcNow));
        var path = Assert.Single(Directory.GetFiles(Path.Combine(directory, "device-usage")));
        File.WriteAllText(path, "invalid json");

        store.Read("ios", "sim");

        Assert.NotNull(store.Warning);
        Assert.Equal("invalid json", File.ReadAllText(path));
    }

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
