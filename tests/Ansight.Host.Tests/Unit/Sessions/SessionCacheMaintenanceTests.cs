using Ansight.Host.Runtime.SessionCaptureStorage;
using Ansight.Host.Tests.TestSupport;

namespace Ansight.Host.Tests.Unit.Sessions;

public sealed class SessionCacheMaintenanceTests
{
    [Fact]
    public void RunOnce_KeepsExpiredSessionBelowLimitAndDeletesItAboveLimit()
    {
        using var environment = new TestEnvironment();
        var store = new SessionCaptureStore(environment.ApplicationPaths);
        var old = CreateSnapshot("old", DateTimeOffset.UtcNow.AddDays(-100));
        SaveWithAdditionalBytes(store, environment, old, 1L * 1_024 * 1_024);

        using (var runtime = environment.CreateRuntime())
        {
            runtime.UserPreferences.SessionAutoCleanupEnabled = true;
            runtime.UserPreferences.SessionAutoCleanupMaximumCacheBytes = 256L * 1_024 * 1_024;
            runtime.UserPreferences.SessionAutoCompactionAgeDays = 365;
            var belowLimit = SessionCacheMaintenance.RunOnce(runtime, CancellationToken.None);
            Assert.Equal(0, belowLimit.DeletedSessionCount);
            Assert.Contains(runtime.Sessions.GetSummaries(), session => session.SessionId == old.SessionId);
        }

        var recent = CreateSnapshot("recent", DateTimeOffset.UtcNow.AddDays(-2));
        SaveWithAdditionalBytes(store, environment, recent, 255L * 1_024 * 1_024);
        using (var runtime = environment.CreateRuntime())
        {
            var overLimit = SessionCacheMaintenance.RunOnce(runtime, CancellationToken.None);
            Assert.Equal(1, overLimit.DeletedSessionCount);
            Assert.DoesNotContain(runtime.Sessions.GetSummaries(), session => session.SessionId == old.SessionId);
            Assert.Contains(runtime.Sessions.GetSummaries(), session => session.SessionId == recent.SessionId);
            Assert.True(overLimit.CacheSizeBytes <= overLimit.MaximumCacheSizeBytes);
        }
    }

    private static AppSessionSnapshot CreateSnapshot(string sessionId, DateTimeOffset createdUtc)
        => new()
        {
            SessionId = sessionId,
            AppId = "com.example.cache",
            ClientName = "Cache test",
            RemoteAddress = "127.0.0.1",
            ConfigId = null,
            CreatedUtc = createdUtc,
            LastUpdatedUtc = createdUtc.AddMinutes(1),
            Status = "Disconnected",
            IsHistorical = true,
            MetricChannels = [],
            Metrics = []
        };

    private static void SaveWithAdditionalBytes(
        SessionCaptureStore store,
        TestEnvironment environment,
        AppSessionSnapshot snapshot,
        long additionalBytes)
    {
        store.Save(snapshot);
        var sessionPath = Path.Combine(
            environment.ApplicationPaths.ApplicationDataPath,
            "session-captures",
            FileNameUtil.Sanitize(snapshot.AppId),
            FileNameUtil.Sanitize(snapshot.SessionId));
        using (var stream = File.Create(Path.Combine(sessionPath, "sparse-test-payload.bin")))
        {
            stream.SetLength(additionalBytes);
        }

        store.Save(snapshot);
    }
}
