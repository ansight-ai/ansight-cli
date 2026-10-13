using Ansight.Adb;

namespace Ansight.Host.Tests.Unit.Devices;

public sealed class DeviceTargetResolverTests
{
    [Theory]
    [InlineData("pixel-serial", "Pixel 7", "device", "pixel-serial")]
    [InlineData("emulator-5554", "Pixel_9", "emulator", "Pixel_9")]
    public async Task ExpiredAndroidTargetUsesAdbWithoutFullDiscovery(
        string serial, string name, string kind, string requestedIdentifier)
    {
        var clock = new TestClock();
        var inventoryCalls = 0;
        var adbCalls = 0;
        var target = new DeviceDescriptor(serial, name, "android", "14", "device", true, true, kind);
        var resolver = new DeviceTargetResolver(
            _ => { inventoryCalls++; return Task.FromResult(new DeviceInventory([], [target], [])); },
            _ => { adbCalls++; return Task.FromResult<IReadOnlyList<AdbDevice>>([new(serial, "device", null, null, null, null)]); },
            clock);

        Assert.Equal(target, await resolver.ResolveAsync(requestedIdentifier, default));
        Assert.Equal(target, await resolver.ResolveAsync(requestedIdentifier, default));
        clock.Advance();
        Assert.Equal(target, await resolver.ResolveAsync(requestedIdentifier, default));

        Assert.Equal(1, inventoryCalls);
        Assert.Equal(1, adbCalls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("offline")]
    [InlineData("unauthorized")]
    public async Task DisconnectIsNotHiddenByCachedMetadataAndReconnectUsesSameSerial(string? state)
    {
        var clock = new TestClock();
        var inventoryCalls = 0;
        var target = new DeviceDescriptor("pixel", "Pixel", "android", "14", "device", true, true, "device");
        var resolver = new DeviceTargetResolver(
            _ => { inventoryCalls++; return Task.FromResult(new DeviceInventory([], [target], [])); },
            _ => Task.FromResult<IReadOnlyList<AdbDevice>>(state is null ? [] : [new("pixel", state, null, null, null, null)]),
            clock);
        await resolver.ResolveAsync("pixel", default);
        clock.Advance();
        var disconnected = await resolver.ResolveAsync("pixel", default);
        Assert.False(disconnected!.IsBooted);
        Assert.Equal(state ?? "disconnected", disconnected.State);

        state = "device";
        clock.Advance();
        Assert.True((await resolver.ResolveAsync("pixel", default))!.IsBooted);
        Assert.Equal(1, inventoryCalls);
    }

    [Fact]
    public async Task IosTargetsStillRefreshFromThePlatformInventory()
    {
        var clock = new TestClock();
        var calls = 0;
        var target = new DeviceDescriptor("ios-udid", "iPhone", "ios", "26", "Booted", true, true, "simulator");
        var resolver = new DeviceTargetResolver(
            _ => { calls++; return Task.FromResult(new DeviceInventory([], [target], [])); },
            _ => throw new InvalidOperationException("An iOS target must not use ADB."), clock);
        await resolver.ResolveAsync("ios-udid", default);
        clock.Advance();
        Assert.Equal(target, await resolver.ResolveAsync("ios-udid", default));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task NamedEmulatorCanRestartOnADifferentSerial()
    {
        var clock = new TestClock();
        var target = new DeviceDescriptor("emulator-5554", "Pixel", "android", "14", "device", true, true, "emulator");
        var resolver = new DeviceTargetResolver(
            _ => Task.FromResult(new DeviceInventory([], [target], [])),
            _ => Task.FromResult<IReadOnlyList<AdbDevice>>([]), clock);
        await resolver.ResolveAsync("Pixel", default);
        target = target with { Identifier = "emulator-5556" };
        clock.Advance();
        Assert.Equal("emulator-5556", (await resolver.ResolveAsync("Pixel", default))!.Identifier);
    }

    private sealed class TestClock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance() => now += TimeSpan.FromSeconds(3);
    }
}
