using Ansight.Host.Runtime.Operations.Tools.SessionEvidence;

namespace Ansight.Host.Tests.Unit.Runtime;

public sealed partial class SessionCaptureStoreTests
{
    [Fact]
    public void SaveAndTryLoad_StoresMotionSeparatelyAndRestoresTimelineCategory()
    {
        using var environment = new TestSupport.TestEnvironment();
        var store = new SessionCaptureStore(environment.ApplicationPaths);
        var capturedAtUtc = DateTimeOffset.Parse("2026-10-06T00:00:00Z");
        var snapshot = new AppSessionSnapshot
        {
            SessionId = "session-motion",
            AppId = "com.example.motion",
            ClientName = "Motion Test",
            RemoteAddress = "127.0.0.1",
            CreatedUtc = capturedAtUtc,
            ConfigId = "config-motion",
            Status = "WebSocket Closed",
            LastUpdatedUtc = capturedAtUtc.AddSeconds(2),
            IsHistorical = false,
            MetricChannels = Array.Empty<SessionMetricChannel>(),
            Metrics = Array.Empty<SessionMetricSample>(),
            ApplicationEvents =
            [
                new SessionApplicationEvent("motion-1", "motion.shake", "Motion", "{}", capturedAtUtc.AddSeconds(1), 0),
                new SessionApplicationEvent("app-1", "auth.login", "flow", "{}", capturedAtUtc.AddSeconds(2), 0)
            ]
        };

        store.Save(snapshot);

        var root = SessionImageArtifactPath.ResolveSessionCapturesRootPath(environment.ApplicationPaths);
        var sessionPath = Path.Combine(root, snapshot.AppId, snapshot.SessionId);
        var motionPath = Path.Combine(sessionPath, "motion", "events.json");
        Assert.True(File.Exists(motionPath));
        Assert.Contains("motion.shake", File.ReadAllText(motionPath));
        Assert.DoesNotContain("motion.shake", File.ReadAllText(Path.Combine(sessionPath, "application-events.json")));

        Assert.True(store.TryLoad(snapshot.SessionId, out var loaded));
        Assert.NotNull(loaded);
        Assert.Equal(2, loaded.ApplicationEvents.Count);
        var timeline = SessionTimelineBuilder.BuildEvents(loaded, null, null);
        Assert.Contains(timeline, item => item.Payload["category"]?.GetValue<string>() == "motion");
        Assert.Contains(timeline, item => item.Payload["category"]?.GetValue<string>() == "applicationEvent");
    }
}
