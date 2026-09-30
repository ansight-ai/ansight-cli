using Ansight.Analytics;

namespace Ansight.Host.Tests.Unit.Runtime;

public sealed partial class SessionCaptureStoreTests
{
    [Fact]
    public void ErrorEndedRecordingPersistsUsageOnceWithEvidenceAndDuration()
    {
        using var environment = new TestSupport.TestEnvironment();
        AnalyticsAccount.Update(environment.RootPath, Guid.NewGuid().ToString("D"));
        new AnalyticsSettingsStore(environment.RootPath).SetDetailedTrackingEnabled(true);
        var store = new SessionCaptureStore(environment.ApplicationPaths);
        var created = DateTimeOffset.UtcNow.AddSeconds(-33);
        AppSessionSnapshot Snapshot(string status, bool historical) => new()
        {
            SessionId = "usage-capture", AppId = "com.example.test", ClientName = "Test",
            RemoteAddress = "127.0.0.1", ConfigId = null, CreatedUtc = created,
            LastUpdatedUtc = created.AddSeconds(33), Status = status, IsHistorical = historical,
            TotalNetworkRequestCount = 159, MetricChannels = [], Metrics = []
        };
        store.Save(Snapshot("WebSocket Open", false));
        store.Save(Snapshot("WebSocket Error", true));
        store.Save(Snapshot("WebSocket Error", true));
        var path = Assert.Single(Directory.GetFiles(Path.Combine(environment.RootPath, "analytics", "usage"), "*.json"));
        var values = JsonNode.Parse(File.ReadAllText(path))!["values"]!;
        Assert.Equal(1, values["host_capture_count"]!.GetValue<int>());
        Assert.Equal(1, values["host_capture_failed"]!.GetValue<int>());
        Assert.Equal(1, values["host_capture_with_evidence"]!.GetValue<int>());
        Assert.Equal(33d, values["host_capture_seconds"]!.GetValue<double>());
    }
}
