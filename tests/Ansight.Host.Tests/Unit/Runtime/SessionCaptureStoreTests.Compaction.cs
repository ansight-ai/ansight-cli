using System.IO.Compression;

namespace Ansight.Host.Tests.Unit.Runtime;

public sealed partial class SessionCaptureStoreTests
{
    [Fact]
    public async Task CompactSessionsOlderThan_ArchivesAndRestoresSessionOnLoad()
    {
        using var environment = new TestSupport.TestEnvironment();
        var nowUtc = DateTimeOffset.Parse("2026-08-21T00:00:00Z");
        var snapshot = CreateCompactionSnapshot(
            "session-old",
            nowUtc.AddDays(-40),
            nowUtc.AddDays(-39),
            "Disconnected");
        var store = new SessionCaptureStore(environment.ApplicationPaths);
        store.Save(snapshot);

        var sessionDirectoryPath = GetSessionDirectoryPath(environment, snapshot);
        File.WriteAllText(
            Path.Combine(sessionDirectoryPath, "compressible-payload.txt"),
            new string('x', 256 * 1024));
        var expandedSizeBytes = Directory
            .EnumerateFiles(sessionDirectoryPath, "*", SearchOption.AllDirectories)
            .Sum(filePath => new FileInfo(filePath).Length);

        var compactedCount = store.CompactSessionsOlderThan(30, nowUtc);

        Assert.Equal(1, compactedCount);
        var archiveFilePath = $"{sessionDirectoryPath}.session-cache.zip";
        Assert.False(Directory.Exists(sessionDirectoryPath));
        Assert.True(File.Exists(archiveFilePath));
        Assert.True(new FileInfo(archiveFilePath).Length < expandedSizeBytes);
        using (var archive = ZipFile.OpenRead(archiveFilePath))
        {
            Assert.NotNull(archive.GetEntry("session.json"));
            Assert.NotNull(archive.GetEntry("logs.json"));
        }

        var restartedStore = new SessionCaptureStore(environment.ApplicationPaths);
        var summary = Assert.Single(restartedStore.LoadSummaries());
        Assert.Equal(snapshot.SessionId, summary.SessionId);
        Assert.Equal(new FileInfo(archiveFilePath).Length, summary.CacheSizeBytes);
        Assert.False(Directory.Exists(sessionDirectoryPath));

        var loaded = await restartedStore.LoadAsync(snapshot.SessionId);

        Assert.NotNull(loaded);
        Assert.Equal(snapshot.SessionId, loaded!.SessionId);
        Assert.Equal("A persisted log entry", Assert.Single(loaded.Logs).Message);
        Assert.True(Directory.Exists(sessionDirectoryPath));
        Assert.True(File.Exists(Path.Combine(sessionDirectoryPath, "compressible-payload.txt")));
        Assert.False(File.Exists(archiveFilePath));

        var reopenedUtc = DateTimeOffset.UtcNow;
        Assert.Equal(0, restartedStore.CompactSessionsOlderThan(30, reopenedUtc.AddDays(29)));
        Assert.Equal(1, restartedStore.CompactSessionsOlderThan(30, reopenedUtc.AddDays(31)));
    }

    [Fact]
    public void CompactSessionsOlderThan_SkipsRecentLiveAndProtectedSessions()
    {
        using var environment = new TestSupport.TestEnvironment();
        var nowUtc = DateTimeOffset.Parse("2026-08-21T00:00:00Z");
        var oldUpdatedUtc = nowUtc.AddDays(-39);
        var store = new SessionCaptureStore(environment.ApplicationPaths);
        var eligible = CreateCompactionSnapshot(
            "session-eligible",
            nowUtc.AddDays(-40),
            oldUpdatedUtc,
            "Disconnected",
            isPinned: true);
        var recent = CreateCompactionSnapshot("session-recent", nowUtc.AddDays(-2), nowUtc.AddDays(-1), "Disconnected");
        var live = CreateCompactionSnapshot("session-live", nowUtc.AddDays(-40), oldUpdatedUtc, "WebSocket Open");
        var protectedSession = CreateCompactionSnapshot("session-protected", nowUtc.AddDays(-40), oldUpdatedUtc, "Disconnected");
        store.Save(eligible);
        store.Save(recent);
        store.Save(live);
        store.Save(protectedSession);

        var compactedCount = store.CompactSessionsOlderThan(
            30,
            nowUtc,
            new HashSet<string>(StringComparer.Ordinal) { protectedSession.SessionId });

        Assert.Equal(1, compactedCount);
        Assert.True(File.Exists($"{GetSessionDirectoryPath(environment, eligible)}.session-cache.zip"));
        Assert.True(Directory.Exists(GetSessionDirectoryPath(environment, recent)));
        Assert.True(Directory.Exists(GetSessionDirectoryPath(environment, live)));
        Assert.True(Directory.Exists(GetSessionDirectoryPath(environment, protectedSession)));

        Assert.True(store.Delete(eligible.SessionId));
        Assert.False(File.Exists($"{GetSessionDirectoryPath(environment, eligible)}.session-cache.zip"));
    }

    private static AppSessionSnapshot CreateCompactionSnapshot(
        string sessionId,
        DateTimeOffset createdUtc,
        DateTimeOffset lastUpdatedUtc,
        string status,
        bool isPinned = false)
    {
        return new AppSessionSnapshot
        {
            SessionId = sessionId,
            AppId = "com.example.compaction",
            ClientName = "Compaction Test Client",
            RemoteAddress = "127.0.0.1",
            CreatedUtc = createdUtc,
            ConfigId = "config-compaction",
            Status = status,
            LastUpdatedUtc = lastUpdatedUtc,
            IsHistorical = !status.Contains("Open", StringComparison.OrdinalIgnoreCase),
            IsPinned = isPinned,
            Logs = [new LogEntry(createdUtc, "A persisted log entry")],
            TotalLogCount = 1,
            MetricChannels = Array.Empty<SessionMetricChannel>(),
            Metrics = Array.Empty<SessionMetricSample>()
        };
    }

    private static string GetSessionDirectoryPath(
        TestSupport.TestEnvironment environment,
        AppSessionSnapshot snapshot)
    {
        return Path.Combine(
            environment.ApplicationPaths.ApplicationDataPath,
            "session-captures",
            FileNameUtil.Sanitize(snapshot.AppId),
            FileNameUtil.Sanitize(snapshot.SessionId));
    }
}
