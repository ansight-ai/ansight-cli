namespace Ansight.Host.Tests.Unit.Runtime;

public sealed partial class SessionCaptureStoreTests
{
    [Fact]
    public async Task LateScreenshotMetadataAndConflictRemovalSurviveReloadAndCompaction()
    {
        using var environment = new TestSupport.TestEnvironment();
        var store = new SessionCaptureStore(environment.ApplicationPaths);
        var capturedAt = DateTimeOffset.UtcNow;
        var saved = await store.SaveSessionImageAsync("geometry.app", "geometry-session", capturedAt, "png", 10, 20, 100, new byte[] { 1, 2, 3 });
        Assert.NotNull(saved);
        AppSessionSnapshot Snapshot(JsonObject? geometry, string status = "Connected") => new()
        {
            SessionId = "geometry-session", AppId = "geometry.app", ClientName = "Test", RemoteAddress = "127.0.0.1",
            CreatedUtc = capturedAt, ConfigId = "test", Status = status, LastUpdatedUtc = capturedAt, IsHistorical = false,
            Images = [new SessionImageFrame { FrameId = saved.FrameId, CapturedAtUtc = capturedAt, Format = "png",
                Width = 10, Height = 20, Quality = 100, ByteCount = 3, Geometry = geometry }], TotalImageCount = 1,
            MetricChannels = [], Metrics = []
        };
        store.Save(Snapshot(null));
        store.Save(Snapshot(ScreenshotInspectionTests.SystemUiGeometry()));
        var reloaded = new SessionCaptureStore(environment.ApplicationPaths);
        Assert.True(reloaded.TryLoad("geometry-session", out var withMetadata));
        Assert.Equal("present", withMetadata!.Images.Single().Geometry?["systemUi"]?["statusBar"]?["state"]?.GetValue<string>());
        store.Save(Snapshot(null)); // Conflicting metadata invalidates a previously paired frame.
        Assert.True(new SessionCaptureStore(environment.ApplicationPaths).TryLoad("geometry-session", out var cleared));
        Assert.Null(cleared!.Images.Single().Geometry);
        store.Save(Snapshot(null, "WebSocket Closed"));
        Assert.True(new SessionCaptureStore(environment.ApplicationPaths).TryLoad("geometry-session", out var closed));
        Assert.Null(closed!.Images.Single().Geometry);
    }
}
