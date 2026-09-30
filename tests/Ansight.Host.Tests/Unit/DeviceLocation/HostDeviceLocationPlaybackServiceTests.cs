using Ansight.Host.Tests.TestSupport;

namespace Ansight.Host.Tests.Unit.DeviceLocation;

public sealed class HostDeviceLocationPlaybackServiceTests
{
    [Fact(Timeout = 10000)]
    public async Task StartAsyncReplaysGpxCoordinatesAndReportsCompletion()
    {
        const string gpx = """
            <?xml version="1.0" encoding="UTF-8"?>
            <gpx version="1.1" xmlns="http://www.topografix.com/GPX/1/1">
              <trk><trkseg>
                <trkpt lat="-33.8688" lon="151.2093"><time>2026-08-17T01:00:00Z</time></trkpt>
                <trkpt lat="-33.8689" lon="151.2094"><time>2026-08-17T01:00:00.100Z</time></trkpt>
              </trkseg></trk>
            </gpx>
            """;
        var devices = new FakeHostDeviceLocationService();
        await using var service = new DeviceLocationPlaybackService(devices);

        var result = await service.StartAsync(new DeviceLocationPlaybackRequest(
            DevicePlatforms.Ios,
            "simulator-001",
            "short-route.gpx",
            gpx,
            PlaybackSpeedMultiplier: 10d));

        Assert.True(result.IsSuccess, result.Message);
        await TestWait.UntilAsync(
            () => service.GetSnapshot().Status == "completed",
            TimeSpan.FromSeconds(3));
        var snapshot = service.GetSnapshot();
        Assert.False(snapshot.IsPlaying);
        Assert.Equal(1, snapshot.CurrentPointIndex);
        Assert.Equal(2, devices.Locations.Count);
        Assert.Equal(-33.8689d, devices.Locations.Last().Latitude, 4);
        Assert.NotNull(snapshot.CompletedUtc);
    }

    [Fact(Timeout = 10000)]
    public async Task StopAsyncCancelsLoopingRoute()
    {
        const string kml = """
            <kml xmlns="http://www.opengis.net/kml/2.2">
              <Document><Placemark><LineString>
                <coordinates>151.2093,-33.8688 151.20931,-33.86881</coordinates>
              </LineString></Placemark></Document>
            </kml>
            """;
        var devices = new FakeHostDeviceLocationService();
        await using var service = new DeviceLocationPlaybackService(devices);
        var result = await service.StartAsync(new DeviceLocationPlaybackRequest(
            DevicePlatforms.Ios,
            "simulator-001",
            "loop.kml",
            kml,
            DeviceLocationPlaybackMode.FixedSpeed,
            FixedSpeedKph: 500d,
            Loop: true));
        Assert.True(result.IsSuccess, result.Message);

        await TestWait.UntilAsync(() => devices.Locations.Count >= 2, TimeSpan.FromSeconds(3));
        var stopped = await service.StopAsync();

        Assert.True(stopped.IsSuccess, stopped.Message);
        Assert.False(service.GetSnapshot().IsPlaying);
        Assert.Equal("stopped", service.GetSnapshot().Status);
    }
}
