using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ansight.Cli.Tests.Analytics;

public sealed class LocalCaptureUsageTests
{
    [Fact]
    public void SavedEvidenceCountsOnceAcrossRetriesRestartsAndOutboxEviction()
    {
        using var directory = TestDirectory.Create();
        var account = Enable(directory.Path);
        var business = Guid.NewGuid().ToString("D");
        var started = DateTimeOffset.UtcNow.AddMinutes(-5);
        var usage = new LocalCaptureUsage(directory.Path);
        Assert.True(usage.Register("local-only-session", started, "sdk", business));
        usage.RecordSavedEvidence("local-only-session", started, started.AddMinutes(1));
        usage.Flush(true);
        var first = Assert.Single(Entries(directory.Path));
        var properties = Properties(first);
        Assert.Equal(1, properties["capture_count"]!.GetValue<int>());
        Assert.Equal(account, properties["account_id"]!.GetValue<string>());
        Assert.Equal(business, properties["business_id"]!.GetValue<string>());
        Assert.DoesNotContain("local-only-session", File.ReadAllText(first.Path));
        Assert.Equal(started.AddMinutes(1).ToString("yyyy-MM-dd"), properties["usage_day"]!.GetValue<string>());

        File.Delete(first.Path); // Delivery or eviction must not permanently lose the latest snapshot.
        usage = new LocalCaptureUsage(directory.Path);
        usage.RecordSavedEvidence("local-only-session", started, DateTimeOffset.UtcNow);
        usage.Flush(true);
        var retry = Assert.Single(Entries(directory.Path));
        Assert.Equal(first.Envelope.InsertId, retry.Envelope.InsertId);
        Assert.Equal(1, Properties(retry)["capture_count"]!.GetValue<int>());
    }

    [Fact]
    public void AccountsConsentAndImportsCannotAcquireEarlierCaptures()
    {
        using var directory = TestDirectory.Create();
        Enable(directory.Path);
        var started = DateTimeOffset.UtcNow.AddMinutes(-1);
        var usage = new LocalCaptureUsage(directory.Path);
        usage.Register("old-owner", started, "sdk");
        Enable(directory.Path); // New account, explicitly opted in.
        usage.RecordSavedEvidence("old-owner", started, DateTimeOffset.UtcNow);
        usage.RecordSavedEvidence("imported", started, DateTimeOffset.UtcNow);
        usage.Flush(true);
        Assert.Empty(Entries(directory.Path));

        usage.Register("before-optout", started, "device");
        new AnalyticsSettingsStore(directory.Path).SetDetailedTrackingEnabled(false);
        Assert.False(usage.Register("while-disabled", started, "device"));
        new AnalyticsSettingsStore(directory.Path).SetDetailedTrackingEnabled(true);
        usage.RecordSavedEvidence("before-optout", started, DateTimeOffset.UtcNow);
        usage.RecordSavedEvidence("while-disabled", started, DateTimeOffset.UtcNow);
        usage.Flush(true);
        Assert.Empty(Entries(directory.Path));
    }

    [Fact]
    public void StaleOperationScopeCannotEraseCurrentAccountCaptures()
    {
        using var directory = TestDirectory.Create();
        var oldAccount = Enable(directory.Path);
        Enable(directory.Path);
        var usage = new LocalCaptureUsage(directory.Path);
        var started = DateTimeOffset.UtcNow.AddMinutes(-1);
        Assert.True(usage.Register("current-capture", started, "sdk"));
        Assert.True(usage.RecordSavedEvidence("current-capture", started, DateTimeOffset.UtcNow));
        using (AnalyticsAccount.Capture(directory.Path, oldAccount)) usage.Flush(true);
        usage.Flush(true);
        Assert.Equal(1, Properties(Assert.Single(Entries(directory.Path)))["capture_count"]!.GetValue<int>());
    }

    [Fact]
    public async Task ConcurrentCapturesKeepTheirOriginalCompaniesAndSources()
    {
        using var directory = TestDirectory.Create();
        Enable(directory.Path);
        var business = Guid.NewGuid().ToString("D");
        var started = DateTimeOffset.UtcNow.AddMinutes(-1);
        await Task.WhenAll(Enumerable.Range(0, 24).Select(index => Task.Run(() =>
        {
            var usage = new LocalCaptureUsage(directory.Path);
            Assert.True(usage.Register($"capture-{index}", started, index % 2 == 0 ? "sdk" : "device", business));
            Assert.True(usage.RecordSavedEvidence($"capture-{index}", started, DateTimeOffset.UtcNow));
        })));
        new LocalCaptureUsage(directory.Path).Flush(true);
        var properties = Properties(Assert.Single(Entries(directory.Path)));
        Assert.Equal(24, properties["capture_count"]!.GetValue<int>());
        Assert.Equal(12, properties["sdk_count"]!.GetValue<int>());
        Assert.Equal(12, properties["device_count"]!.GetValue<int>());
    }

    [Fact]
    public void PendingReceiptsSurviveFailedLedgerUpdates()
    {
        using var directory = TestDirectory.Create();
        Enable(directory.Path);
        var usage = new LocalCaptureUsage(directory.Path);
        var started = DateTimeOffset.UtcNow.AddMinutes(-1);
        usage.Register("retry", started, "device");
        usage.RecordSavedEvidence("retry", started, DateTimeOffset.UtcNow);
        var path = Path.Combine(directory.Path, "analytics", "local-captures", "state.json");
        var original = File.ReadAllText(path);
        File.WriteAllText(path, "invalid JSON");
        usage.Flush(true);
        Assert.Empty(Entries(directory.Path));
        Assert.Single(Directory.GetFiles(Path.Combine(directory.Path, "analytics", "local-captures", "pending")));
        File.WriteAllText(path, original);
        usage.Flush(true);
        Assert.Equal(1, Properties(Assert.Single(Entries(directory.Path)))["device_count"]!.GetValue<int>());
    }

    [Fact]
    public void VerifiedCompanyContextIsExplicitAccountScopedAndAmbiguityStaysUnassigned()
    {
        using var directory = TestDirectory.Create();
        var account = Enable(directory.Path);
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var personal = Guid.NewGuid();
        using var teams = JsonDocument.Parse(JsonSerializer.Serialize(new[]
        {
            new { id = first, workspace_kind = "organisation", allowed_app_ids = new[] { "app" } },
            new { id = second, workspace_kind = "organisation", allowed_app_ids = new[] { "*" } },
            new { id = personal, workspace_kind = "personal", allowed_app_ids = new[] { "*" } }
        }));
        CaptureBusinessAttribution.SaveVerifiedMemberships(directory.Path, account, teams.RootElement);
        Assert.Equal(first.ToString("D"), CaptureBusinessAttribution.Resolve(directory.Path, "app", first.ToString("D")));
        Assert.Null(CaptureBusinessAttribution.Resolve(directory.Path, "other", first.ToString("D")));
        Assert.Null(CaptureBusinessAttribution.Resolve(directory.Path, "app", personal.ToString("D")));
        using (CaptureBusinessAttribution.Select(directory.Path, "app", second))
        {
            Assert.Equal(second.ToString("D"), CaptureBusinessAttribution.Resolve(directory.Path, "app", first.ToString("D")));
            using (CaptureBusinessAttribution.Select(directory.Path, "app", first))
                Assert.Null(CaptureBusinessAttribution.Resolve(directory.Path, "app", first.ToString("D")));
        }
        Enable(directory.Path);
        Assert.Null(CaptureBusinessAttribution.Resolve(directory.Path, "app", first.ToString("D")));
    }

    private static string Enable(string directory)
    {
        var account = Guid.NewGuid().ToString("D");
        AnalyticsAccount.Update(directory, account);
        new AnalyticsSettingsStore(directory).SetDetailedTrackingEnabled(true);
        return account;
    }
    private static IReadOnlyList<OutboxEntry> Entries(string directory) => new EventOutbox(Path.Combine(directory, "analytics"))
        .Load(250).Where(entry => entry.Envelope.EventName == LocalCaptureUsage.EventName).ToArray();
    private static JsonObject Properties(OutboxEntry entry) => JsonNode.Parse(File.ReadAllText(entry.Path))!["properties"]!.AsObject();
}
