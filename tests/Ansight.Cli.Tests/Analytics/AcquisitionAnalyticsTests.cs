using Ansight.Analytics;

namespace Ansight.Cli.Tests.Analytics;

public sealed class AcquisitionAnalyticsTests
{
    [Fact]
    public void OptOutSuppressesAccountAndMilestoneEvents()
    {
        using var directory = TestDirectory.Create();
        var settings = new AnalyticsSettingsStore(directory.Path);
        settings.SetDetailedTrackingEnabled(false);
        AcquisitionAnalytics.Track(directory.Path, "cli_auth_completed", Guid.NewGuid().ToString(), Guid.NewGuid().ToString(), "succeeded");
        AcquisitionAnalytics.Track(directory.Path, "cli_first_app_connected", once: true);
        Assert.Empty(new EventOutbox(settings.AnalyticsDirectoryPath).Load(100));
        Assert.Null(AcquisitionAnalytics.Journey(directory.Path));
    }

    [Fact]
    public void AuthenticationAcceptsOnlyOpaqueIdsAndFixedOutcomes()
    {
        using var directory = TestDirectory.Create();
        var settings = new AnalyticsSettingsStore(directory.Path);
        var signedInAccount = Guid.NewGuid().ToString("D");
        AnalyticsAccount.Update(directory.Path, signedInAccount);
        settings.SetDetailedTrackingEnabled(true);
        AcquisitionAnalytics.Track(directory.Path, "cli_auth_failed", "state-secret", "user@example.com", "token=secret");
        var entry = Assert.Single(new EventOutbox(settings.AnalyticsDirectoryPath).Load(100));
        Assert.Equal(signedInAccount, entry.Envelope.Properties["account_id"]?.ToString());
        Assert.False(entry.Envelope.Properties.ContainsKey("auth_attempt_id"));
        Assert.False(entry.Envelope.Properties.ContainsKey("outcome"));
        Assert.DoesNotContain("secret", File.ReadAllText(entry.Path));
        Assert.DoesNotContain("example.com", File.ReadAllText(entry.Path));
    }

    [Fact]
    public void ValidatedSignInLinksInstallationToAccountWithoutChangingInstallationIdentity()
    {
        using var directory = TestDirectory.Create();
        var settings = new AnalyticsSettingsStore(directory.Path);
        var attempt = Guid.NewGuid().ToString("D");
        var account = Guid.NewGuid().ToString("D");
        AcquisitionAnalytics.Track(directory.Path, "cli_auth_started", attempt);
        AnalyticsAccount.Update(directory.Path, account);
        settings.SetDetailedTrackingEnabled(true);
        AcquisitionAnalytics.Track(directory.Path, "cli_auth_completed", attempt, account, "succeeded");
        var entries = new EventOutbox(settings.AnalyticsDirectoryPath).Load(100);
        Assert.Equal(2, entries.Count);
        var acquisition = entries.Where(item => item.Envelope.EventName != "cli_account_linked").ToArray();
        Assert.Single(acquisition.Select(item => item.Envelope.DistinctId).Distinct());
        Assert.All(acquisition, item => Assert.Equal(attempt, item.Envelope.Properties["auth_attempt_id"]?.ToString()));
        Assert.Equal(account, acquisition.Single(item => item.Envelope.EventName == "cli_auth_completed").Envelope.Properties["account_id"]?.ToString());
        var identity = entries.Single(item => item.Envelope.EventName == "cli_account_linked").Envelope;
        Assert.Equal(AnalyticsIdentity.LoadOrCreate(settings.AnalyticsDirectoryPath), identity.DistinctId);
        Assert.Equal(acquisition[0].Envelope.DistinctId, identity.Properties["installation_id"]?.ToString());
        Assert.Equal("cli", identity.Properties["surface"]?.ToString());
    }

    [Fact]
    public void HostStartupIdentityIsDeterministicAndAttributedToHost()
    {
        using var directory = TestDirectory.Create();
        var account = Guid.NewGuid().ToString("D");
        AnalyticsAccount.Update(directory.Path, account);
        new AnalyticsSettingsStore(directory.Path).SetDetailedTrackingEnabled(true);
        var analytics = new ProductAnalytics(directory.Path);

        AcquisitionAnalytics.IdentifyAccount(directory.Path, account, "secret-surface");
        Assert.Empty(new EventOutbox(new AnalyticsSettingsStore(directory.Path).AnalyticsDirectoryPath).Load(100));
        analytics.IdentifyAccount(account);
        analytics.IdentifyAccount(account);

        var settings = new AnalyticsSettingsStore(directory.Path);
        var identity = Assert.Single(new EventOutbox(settings.AnalyticsDirectoryPath).Load(100)).Envelope;
        Assert.Equal("cli_account_linked", identity.EventName);
        Assert.Equal(AnalyticsIdentity.LoadOrCreate(settings.AnalyticsDirectoryPath), identity.DistinctId);
        Assert.Equal("host", identity.Properties["surface"]?.ToString());
    }

    [Fact]
    public void FirstMilestoneDoesNotRepeatAfterSuccessfulDelivery()
    {
        using var directory = TestDirectory.Create();
        AnalyticsAccount.Update(directory.Path, Guid.NewGuid().ToString("D"));
        var settings = new AnalyticsSettingsStore(directory.Path);
        settings.SetDetailedTrackingEnabled(true);
        var outbox = new EventOutbox(settings.AnalyticsDirectoryPath);
        AcquisitionAnalytics.Track(directory.Path, "cli_first_app_connected", once: true);
        var queued = Assert.Single(outbox.Load(100));
        outbox.Remove(queued.Path);
        AcquisitionAnalytics.Track(directory.Path, "cli_first_app_connected", once: true);
        Assert.Empty(outbox.Load(100));
    }
}
