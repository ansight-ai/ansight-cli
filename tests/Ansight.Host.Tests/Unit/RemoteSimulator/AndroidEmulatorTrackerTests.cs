using Ansight.Adb;
using Ansight.RemoteSimulator.Core.Simulator.Android;

namespace Ansight.Host.Tests.Unit.RemoteSimulator;

public sealed class AndroidEmulatorTrackerTests
{
    [Fact]
    public async Task RefreshAsync_MapsBootedEmulatorsAndIgnoresPhysicalDevices()
    {
        var client = new FakeAndroidEmulatorClient
        {
            Devices =
            [
                new AdbDevice("emulator-5554", "device", "sdk_phone", "sdk_gphone64_arm64", "emu64a", "1"),
                new AdbDevice("R3CR30PHONE", "device", "phone", "Physical Phone", "phone", "2"),
            ],
        };
        client.Responses["-s emulator-5554 shell getprop"] = Success("""
            [ro.boot.qemu.avd_name]: [Pixel_9_API_35]
            [ro.build.version.sdk]: [35]
            [sys.boot_completed]: [1]
            """);
        client.Responses["-s emulator-5554 shell wm size"] = Success("""
            Physical size: 1080x2400
            Override size: 1000x2200
            """);
        client.Responses["-s emulator-5554 emu avd name"] = Success("Pixel_9_API_35\nOK\n");

        await using var tracker = new AndroidEmulatorTracker(client);

        var snapshot = await tracker.RefreshAsync();

        var device = Assert.Single(snapshot.Devices);
        Assert.Equal("emulator-5554", device.Identifier);
        Assert.Equal("Pixel 9 API 35", device.Name);
        Assert.Equal("Android API 35", device.RuntimeIdentifier);
        Assert.Equal("android", device.Platform);
        Assert.True(device.IsBooted);
        Assert.Equal(1000, device.DisplayWidth);
        Assert.Equal(2200, device.DisplayHeight);
    }

    [Fact]
    public void ParseDisplaySize_PrefersLastReportedOverride()
    {
        var result = AndroidEmulatorTracker.ParseDisplaySize("""
            Physical size: 1440x3120
            Override size: 1080x2340
            """);

        Assert.Equal(new AndroidDisplaySize(1080, 2340), result);
    }

    private static AdbCommandResult Success(string output)
        => new(0, output, string.Empty);
}
