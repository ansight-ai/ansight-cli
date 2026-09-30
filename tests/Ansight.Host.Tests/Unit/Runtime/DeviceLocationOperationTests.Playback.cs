using Ansight.Host.Runtime.Operations;
using Ansight.Host.Tests.TestSupport;

namespace Ansight.Host.Tests.Unit.Runtime;

public sealed partial class DeviceLocationOperationTests
{
    private const string PlaybackGpx = """
        <gpx version="1.1"><trk><trkseg>
          <trkpt lat="-35.094" lon="150.804"><time>2026-09-09T00:00:00Z</time></trkpt>
          <trkpt lat="-35.095" lon="150.805"><time>2026-09-09T00:01:00Z</time></trkpt>
        </trkseg></trk></gpx>
        """;

    [Fact]
    public async Task PlayLocation_ReportsFixedSpeedFallbackAndDefaults()
    {
        using var environment = new TestEnvironment();
        var state = new RuntimeState(new SessionCaptureStore(environment.ApplicationPaths));
        var locations = new FakeHostDeviceLocationService();
        await using var playback = new DeviceLocationPlaybackService(locations);
        var catalog = CreateToolCatalog(state, environment, new TestAppToolBridge(), null, playback, PlaybackDevices());
        var response = await catalog.HandleToolsCallAsync(new JsonObject
        {
            ["name"] = "ansight_play_device_location",
            ["arguments"] = new JsonObject
            {
                ["deviceId"] = "device-1",
                ["routeContent"] = "<gpx><trk><trkseg><trkpt lat='0' lon='0'/><trkpt lat='1' lon='1'/></trkseg></trk></gpx>"
            }
        });

        var result = GetStructuredContent(response);
        Assert.Equal("fixed-speed", result["mode"]!.GetValue<string>());
        Assert.Equal("track.gpx", result["sourceFileName"]!.GetValue<string>());
        Assert.Equal(30d, result["fixedSpeedKph"]!.GetValue<double>());
        Assert.Equal(1d, result["speed"]!.GetValue<double>());
        Assert.False(result["loop"]!.GetValue<bool>());
        Assert.Equal(2, result["pointCount"]!.GetValue<int>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PlayLocation_RequiresTargetAndConfiguredHost(bool provideTarget)
    {
        using var environment = new TestEnvironment();
        var state = new RuntimeState(new SessionCaptureStore(environment.ApplicationPaths));
        var catalog = CreateToolCatalog(state, environment, new TestAppToolBridge(), null);
        var arguments = new JsonObject { ["routeContent"] = PlaybackGpx };
        if (provideTarget) arguments["deviceId"] = "device-1";
        var response = await catalog.HandleToolsCallAsync(new JsonObject
        {
            ["name"] = "ansight_play_device_location", ["arguments"] = arguments
        });

        var result = GetStructuredContent(response, expectedToolError: true);
        Assert.Contains(provideTarget ? "no location playback service" : "live session or explicit deviceId",
            result["message"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("ios", "simulator", "recorded")]
    [InlineData("android", "emulator", "fixed-speed")]
    public async Task PlayLocation_UsesLiveSessionDeviceAndSharedPlaybackService(string platform, string kind, string mode)
    {
        using var environment = new TestEnvironment();
        var state = new RuntimeState(new SessionCaptureStore(environment.ApplicationPaths));
        var bridge = new TestAppToolBridge();
        var sessionId = state.CreateSession("com.example.location", "Location App", IPAddress.Loopback, null, null);
        state.SetSessionDeviceProfile(sessionId, null, """{"device":{"nativeDeviceId":"device-1"}}""");
        bridge.Connect(sessionId);
        var locations = new FakeHostDeviceLocationService();
        await using var playback = new DeviceLocationPlaybackService(locations);
        var devices = PlaybackDevices(platform, kind);
        var catalog = CreateToolCatalog(state, environment, bridge, null, playback, devices);

        var response = await catalog.HandleToolsCallAsync(new JsonObject
        {
            ["name"] = "ansight_play_device_location",
            ["arguments"] = new JsonObject
            {
                ["sessionId"] = sessionId,
                ["routeContent"] = PlaybackGpx,
                ["sourceFileName"] = "approach.gpx",
                ["mode"] = mode,
                ["speed"] = 4,
                ["fixedSpeedKph"] = 20,
                ["loop"] = true
            }
        });

        var result = GetStructuredContent(response);
        var snapshot = playback.GetSnapshot();
        Assert.Equal(snapshot.RunId, result["runId"]!.GetValue<string>());
        Assert.Equal(sessionId, result["sessionId"]!.GetValue<string>());
        Assert.Equal("device-1", snapshot.DeviceIdentifier);
        Assert.Equal(platform, snapshot.Platform);
        Assert.Equal(mode, result["mode"]!.GetValue<string>());
        Assert.Equal(4, snapshot.PlaybackSpeedMultiplier);
        Assert.Equal(20, snapshot.FixedSpeedKph);
        Assert.True(snapshot.Loop);
        Assert.True(snapshot.IsPlaying);
        Assert.Equal(-35.094, locations.Locations.First().Latitude);
        Assert.True((await playback.StopAsync()).IsSuccess);
        Assert.Equal("stopped", playback.GetSnapshot().Status);
    }

    [Theory]
    [InlineData("routeContent", "<gpx>broken")]
    [InlineData("routeContent", "<gpx><trk><trkseg><trkpt lat='0' lon='0'/></trkseg></trk></gpx>")]
    [InlineData("mode", "fast")]
    [InlineData("speed", 0)]
    [InlineData("fixedSpeedKph", 501)]
    [InlineData("loop", "yes")]
    [InlineData("deviceId", "unknown-device")]
    public async Task PlayLocation_InvalidArgumentsDoNotMoveDevice(string property, object value)
    {
        using var environment = new TestEnvironment();
        var state = new RuntimeState(new SessionCaptureStore(environment.ApplicationPaths));
        var locations = new FakeHostDeviceLocationService();
        await using var playback = new DeviceLocationPlaybackService(locations);
        var catalog = CreateToolCatalog(state, environment, new TestAppToolBridge(), null, playback, PlaybackDevices());
        var arguments = new JsonObject { ["deviceId"] = "device-1", ["routeContent"] = PlaybackGpx };
        arguments[property] = JsonSerializer.SerializeToNode(value);

        var result = await catalog.HandleToolsCallAsync(new JsonObject
        {
            ["name"] = "ansight_play_device_location", ["arguments"] = arguments
        });

        GetStructuredContent(result, expectedToolError: true);
        Assert.Empty(locations.Locations);
        Assert.Equal("idle", playback.GetSnapshot().Status);
    }

    [Theory]
    [InlineData("device", true)]
    [InlineData("simulator", false)]
    public async Task PlayLocation_RejectsPhysicalAndUnbootedTargets(string kind, bool booted)
    {
        using var environment = new TestEnvironment();
        var state = new RuntimeState(new SessionCaptureStore(environment.ApplicationPaths));
        var locations = new FakeHostDeviceLocationService();
        await using var playback = new DeviceLocationPlaybackService(locations);
        var devices = PlaybackDevices(kind: kind, booted: booted);
        var catalog = CreateToolCatalog(state, environment, new TestAppToolBridge(), null, playback, devices);

        var response = await catalog.HandleToolsCallAsync(new JsonObject
        {
            ["name"] = "ansight_play_device_location",
            ["arguments"] = new JsonObject { ["deviceId"] = "device-1", ["routeContent"] = PlaybackGpx }
        });

        GetStructuredContent(response, expectedToolError: true);
        Assert.Empty(locations.Locations);
    }

    private static FakeHostDeviceService PlaybackDevices(string platform = "ios", string kind = "simulator", bool booted = true)
        => new()
        {
            Inventory = new DeviceInventory([], [new DeviceDescriptor(
                "device-1", "Test device", platform, "test", booted ? "Booted" : "Shutdown", booted, true, kind)], [])
        };
}
