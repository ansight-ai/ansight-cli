namespace Ansight.Host.Tests.Integration;

public sealed class SharedBackingDataTests
{
    [Fact]
    public void HostAndDaemonStyleInstances_SeeSharedSessionsPairingsAndKnownApps()
    {
        using var environment = new TestSupport.TestEnvironment();

        using var firstRuntime = environment.CreateRuntime();
        var firstKnownApps = new KnownAppStore(environment.ApplicationPaths);
        var firstPairingCache = new PairingConfigCache(new FileBackedEncryptedStorage(environment.SecureStorageFilePath));

        Assert.Empty(firstRuntime.Sessions.GetSummaries());
        Assert.Empty(firstKnownApps.GetSnapshot());
        Assert.Empty(firstPairingCache.GetSnapshot());

        using var daemonIdentityStore = new IdentityStore(
            environment.ApplicationPaths,
            new FileBackedEncryptedStorage(environment.SecureStorageFilePath));
        var daemonStorage = new FileBackedEncryptedStorage(environment.SecureStorageFilePath);
        var daemonPairingCache = new PairingConfigCache(daemonStorage);
        var daemonPairingService = new PairingConfigService(
            environment.ApplicationPaths,
            daemonIdentityStore,
            daemonPairingCache);
        var daemonKnownApps = new KnownAppStore(environment.ApplicationPaths);
        var daemonSessionStore = new SessionCaptureStore(environment.ApplicationPaths);

        daemonKnownApps.EnsureKnown(
            "com.example.testapp",
            "Example Test App",
            "glyph-a",
            "/tmp/example",
            DateTimeOffset.Parse("2026-03-20T00:00:00Z"));

        var pairingResult = daemonPairingService.Issue(
            "Example Test App",
            "com.example.testapp",
            PairingConfigDuration.Default);

        daemonSessionStore.Save(new AppSessionSnapshot
        {
            SessionId = "session-001",
            AppId = "com.example.testapp",
            ClientName = "iPhone 15 Pro",
            RemoteAddress = "127.0.0.1",
            CreatedUtc = DateTimeOffset.Parse("2026-03-20T00:05:00Z"),
            LastUpdatedUtc = DateTimeOffset.Parse("2026-03-20T00:10:00Z"),
            ConfigId = pairingResult.ConfigId,
            Status = "Captured",
            IsHistorical = true,
            Logs = Array.Empty<LogEntry>(),
            MetricChannels = Array.Empty<SessionMetricChannel>(),
            Metrics = Array.Empty<SessionMetricSample>()
        });

        var knownApp = Assert.Single(firstKnownApps.GetSnapshot());
        Assert.Equal("com.example.testapp", knownApp.AppId);
        Assert.Equal("Example Test App", knownApp.Name);

        var pairingConfig = Assert.Single(firstPairingCache.GetSnapshot());
        Assert.Equal(pairingResult.ConfigId, pairingConfig.Config.ConfigId);
        Assert.Equal("com.example.testapp", pairingConfig.Config.AppId);

        var sessionSummary = Assert.Single(firstRuntime.Sessions.GetSummaries());
        Assert.Equal("session-001", sessionSummary.SessionId);
        Assert.Equal("com.example.testapp", sessionSummary.AppId);

        var loaded = firstRuntime.Sessions.TryGetSnapshot("session-001", out var sessionSnapshot);
        Assert.True(loaded);
        Assert.NotNull(sessionSnapshot);
        Assert.Equal(pairingResult.ConfigId, sessionSnapshot!.ConfigId);
        Assert.Equal("Captured", sessionSnapshot.Status);
    }
}
