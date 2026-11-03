using Ansight.Analytics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ansight.Host.Tests.Unit.Runtime;

public sealed class LocalCaptureUsageIntegrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LocalCreationRecordsEvidenceBeforeCompletionAndReconnectDoesNotCountAgain(bool device)
    {
        using var environment = new TestSupport.TestEnvironment();
        var account = Enable(environment.RootPath);
        var store = new SessionCaptureStore(environment.ApplicationPaths);
        var state = new RuntimeState(store);
        var id = device
            ? state.CreateDeviceSession(new WorkspaceTestTarget("ios", "device", "Phone", "test.app", false, false, false)
                { DeviceKind = DeviceKinds.Physical, ExecutionMode = "device" })
            : state.CreateSession("test.app", "Client", IPAddress.Loopback, null, "process");
        state.AddSessionLog(id, new LogEntry(DateTimeOffset.UtcNow, "Captured evidence"));
        Assert.True(state.TryGetSessionSnapshot(id, out var snapshot));
        store.Save(snapshot!);
        new LocalCaptureUsage(environment.RootPath).Flush(true);
        var first = Assert.Single(CaptureEvents(environment.RootPath));
        Assert.Equal(1, first[device ? "device_count" : "sdk_count"]!.GetValue<int>());
        Assert.Equal(account, first["account_id"]!.GetValue<string>());
        Assert.DoesNotContain(account, JsonSerializer.Serialize(snapshot));

        if (device) state.EndDeviceSession(id);
        else
        {
            state.SetSessionStatus(id, "WebSocket Closed");
            Assert.Equal(id, state.CreateSession("test.app", "Client", IPAddress.Loopback, null, "process"));
            state.SetSessionStatus(id, "WebSocket Error");
        }
        Assert.True(state.TryGetSessionSnapshot(id, out snapshot));
        store.Save(snapshot!);
        new LocalCaptureUsage(environment.RootPath).Flush(true);
        Assert.Equal(1, Assert.Single(CaptureEvents(environment.RootPath))["capture_count"]!.GetValue<int>());
    }

    [Fact]
    public void ImportedSnapshotsAndCapturesStartedBeforeConsentAreExcluded()
    {
        using var environment = new TestSupport.TestEnvironment();
        var store = new SessionCaptureStore(environment.ApplicationPaths);
        var state = new RuntimeState(store);
        var id = state.CreateSession("test.app", "Client", IPAddress.Loopback, null, null);
        Enable(environment.RootPath);
        state.AddSessionLog(id, new LogEntry(DateTimeOffset.UtcNow, "Captured after opt-in"));
        Assert.True(state.TryGetSessionSnapshot(id, out var snapshot));
        store.Save(snapshot!);
        store.Save(Snapshot("imported", DateTimeOffset.UtcNow.AddMinutes(-1)));
        new LocalCaptureUsage(environment.RootPath).Flush(true);
        Assert.Empty(CaptureEvents(environment.RootPath));
    }

    [Fact]
    public void SavedSummaryRecoversReceiptAfterRestartAndSurvivesCacheDeletion()
    {
        using var environment = new TestSupport.TestEnvironment();
        Enable(environment.RootPath);
        var store = new SessionCaptureStore(environment.ApplicationPaths);
        var created = DateTimeOffset.UtcNow.AddMinutes(-1);
        store.RegisterLocalCapture("recovery", created, "sdk", null);
        store.Save(Snapshot("recovery", created));
        var pending = Path.Combine(environment.RootPath, "analytics", "local-captures", "pending");
        foreach (var path in Directory.GetFiles(pending)) File.Delete(path);
        _ = new SessionCaptureStore(environment.ApplicationPaths);
        Assert.Single(Directory.GetFiles(pending));
        Directory.Delete(store.CapturesRootPath, recursive: true);
        new LocalCaptureUsage(environment.RootPath).Flush(true);
        Assert.Equal(1, Assert.Single(CaptureEvents(environment.RootPath))["capture_count"]!.GetValue<int>());
    }

    [Fact]
    public void AccountSwitchDuringCaptureNeverAttributesOldEvidenceToTheNewAccount()
    {
        using var environment = new TestSupport.TestEnvironment();
        Enable(environment.RootPath);
        var store = new SessionCaptureStore(environment.ApplicationPaths);
        var created = DateTimeOffset.UtcNow.AddMinutes(-1);
        store.RegisterLocalCapture("account-switch", created, "sdk", null);
        Enable(environment.RootPath);
        store.Save(Snapshot("account-switch", created));
        new LocalCaptureUsage(environment.RootPath).Flush(true);
        Assert.Empty(CaptureEvents(environment.RootPath));
    }

    private static AppSessionSnapshot Snapshot(string id, DateTimeOffset created) => new()
    {
        SessionId = id, AppId = "test.app", ClientName = "Client", RemoteAddress = "localhost", ConfigId = null,
        CreatedUtc = created, LastUpdatedUtc = DateTimeOffset.UtcNow, Status = "WebSocket Closed", IsHistorical = true,
        TotalNetworkRequestCount = 1, MetricChannels = [], Metrics = []
    };

    private static string Enable(string directory)
    {
        var account = Guid.NewGuid().ToString("D");
        AnalyticsAccount.Update(directory, account);
        new AnalyticsSettingsStore(directory).SetDetailedTrackingEnabled(true);
        return account;
    }
    private static IReadOnlyList<JsonObject> CaptureEvents(string directory) => new EventOutbox(Path.Combine(directory, "analytics"))
        .Load(250).Where(entry => entry.Envelope.EventName == LocalCaptureUsage.EventName)
        .Select(entry => JsonNode.Parse(File.ReadAllText(entry.Path))!["properties"]!.AsObject()).ToArray();
}
