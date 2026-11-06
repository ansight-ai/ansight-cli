using Ansight.Host.Tests.TestSupport;
using Ansight.RemoteSimulator.Core.Devices;

namespace Ansight.Host.Tests.Unit.Devices;

public sealed class CompanionRoutePlaybackServiceTests
{
    private const string Gpx = "<gpx><trk><trkseg><trkpt lat='-33' lon='151'/><trkpt lat='-34' lon='152'/></trkseg></trk></gpx>";

    [Fact]
    public async Task Route_RejectsOtherTargetsAndStopsOnLostContact()
    {
        var clock = new TestClock();
        var devices = new FakeHostDeviceLocationService();
        await using var playback = new DeviceLocationPlaybackService(devices);
        var service = new CompanionRoutePlaybackService(playback, clock);
        var started = await service.ExecuteAsync(new RemoteDeviceTestingRequest(
            "sim", "route-start", RouteContent: Gpx, Platform: "ios"));
        Assert.True(started.IsSuccess, started.Message);

        var runId = started.Details!["runId"]!.GetValue<string>();

        var wrongTarget = await service.ExecuteAsync(new RemoteDeviceTestingRequest(
            "other", "route-stop", RunId: runId));
        Assert.False(wrongTarget.IsSuccess);
        Assert.True(playback.GetSnapshot().IsPlaying);

        clock.Advance(TimeSpan.FromSeconds(16));
        await service.CheckTimeoutAsync();
        Assert.False(playback.GetSnapshot().IsPlaying);

        var countAfterStop = devices.Locations.Count;
        await Task.Delay(100);
        Assert.Equal(countAfterStop, devices.Locations.Count);
    }

    [Fact]
    public async Task Stop_IsIdempotentAndDoesNotCancelAReplacementRoute()
    {
        await using var playback = new DeviceLocationPlaybackService(new FakeHostDeviceLocationService());
        var service = new CompanionRoutePlaybackService(playback);
        var started = await service.ExecuteAsync(new RemoteDeviceTestingRequest(
            "sim", "route-start", RouteContent: Gpx, Platform: "ios"));
        var runId = started.Details!["runId"]!.GetValue<string>();
        var stopRequest = new RemoteDeviceTestingRequest("sim", "route-stop", RunId: runId);
        Assert.True((await service.ExecuteAsync(stopRequest)).IsSuccess);
        Assert.True((await service.ExecuteAsync(stopRequest)).IsSuccess);

        var other = await playback.StartAsync(new DeviceLocationPlaybackRequest("ios", "other", "test.gpx", Gpx));
        Assert.True(other.IsSuccess);
        Assert.True((await service.ExecuteAsync(stopRequest)).IsSuccess);
        Assert.True(playback.GetSnapshot().IsPlaying);
        Assert.Equal("other", playback.GetSnapshot().DeviceIdentifier);
    }

    [Fact]
    public async Task Start_DoesNotReplaceExistingPlaybackAndRejectsXmlEntities()
    {
        await using var playback = new DeviceLocationPlaybackService(new FakeHostDeviceLocationService());
        var service = new CompanionRoutePlaybackService(playback);
        var existing = await playback.StartAsync(new DeviceLocationPlaybackRequest("ios", "other", "test.gpx", Gpx));
        var refused = await service.ExecuteAsync(new RemoteDeviceTestingRequest("sim", "route-start", RouteContent: Gpx, Platform: "ios"));
        Assert.False(refused.IsSuccess);
        Assert.Equal(existing.Playback.RunId, playback.GetSnapshot().RunId);

        var invalid = await service.ExecuteAsync(new RemoteDeviceTestingRequest("sim", "route-start",
            RouteContent: "<!DOCTYPE gpx [<!ENTITY external SYSTEM 'file:///etc/passwd'>]><gpx>&external;</gpx>", Platform: "ios"));
        Assert.False(invalid.IsSuccess);
    }

    private sealed class TestClock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.UtcNow;

        public override DateTimeOffset GetUtcNow() => now;

        public void Advance(TimeSpan duration)
        {
            now += duration;
        }
    }
}
