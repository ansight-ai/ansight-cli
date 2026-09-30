using AppLifecycleState = global::Ansight.AppLifecycleState;

namespace Ansight.Host.Tests.Unit.Runtime;

public sealed partial class SessionCaptureStoreTests
{
    [Fact]
    public void SaveSessionAppIcon_WritesDecodedIconAndReturnsMetadata()
    {
        using var environment = new TestSupport.TestEnvironment();
        var store = new SessionCaptureStore(environment.ApplicationPaths);
        var bytes = new byte[] { 1, 2, 3 };

        var icon = store.SaveSessionAppIcon(
            "com.example.testapp",
            "session-icon",
            new DeviceApplicationIconProfile
            {
                Format = "png",
                MimeType = "image/png",
                Width = 2,
                Height = 2,
                ByteCount = bytes.Length,
                DataBase64 = Convert.ToBase64String(bytes)
            });

        Assert.NotNull(icon);
        Assert.Equal("app-icon.png", icon!.FileName);
        Assert.Equal(bytes.Length, icon.ByteCount);
        var iconPath = SessionAppIconArtifactPath.ResolveSessionIconPath(
            SessionAppIconArtifactPath.ResolveSessionCapturesRootPath(environment.ApplicationPaths),
            "com.example.testapp",
            "session-icon",
            icon);
        Assert.True(File.Exists(iconPath));
        Assert.Equal(bytes, File.ReadAllBytes(iconPath));
    }

    [Fact]
    public void Save_DoesNotRefreshCacheSizeForRecordingSession()
    {
        using var environment = new TestSupport.TestEnvironment();
        var store = new SessionCaptureStore(environment.ApplicationPaths);
        var createdUtc = DateTimeOffset.Parse("2026-03-20T00:00:00Z");
        var snapshot = new AppSessionSnapshot
        {
            SessionId = "session-live-cache-size",
            AppId = "com.example.testapp",
            ClientName = "Integration Client",
            RemoteAddress = "127.0.0.1",
            CreatedUtc = createdUtc,
            ConfigId = "config-001",
            Status = "WebSocket Open",
            LastUpdatedUtc = createdUtc.AddMinutes(1),
            IsHistorical = false,
            CacheSizeBytes = 17,
            Logs =
            [
                new LogEntry(createdUtc, "This live session log makes the persisted folder larger than the stale cache size.")
            ],
            MetricChannels = Array.Empty<SessionMetricChannel>(),
            Metrics = Array.Empty<SessionMetricSample>()
        };

        store.Save(snapshot);

        var summary = Assert.Single(store.LoadSummaries());
        Assert.Equal(snapshot.CacheSizeBytes, summary.CacheSizeBytes);
        Assert.True(store.TryGetSessionCacheSizeBytes(snapshot.SessionId, out var lazyCacheSizeBytes));
        Assert.True(lazyCacheSizeBytes > snapshot.CacheSizeBytes);
    }
}
