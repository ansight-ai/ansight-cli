using System.Net;
using System.Text.Json.Nodes;
using Ansight.Analytics;
using Ansight.Host.Telemetry;
using Ansight.Host.Tests.Unit.Runtime;

namespace Ansight.Host.Tests.Unit.Analytics;

public sealed class AnonymousAnalyticsDeliveryTests
{
    [Fact]
    public async Task AcknowledgedCaptureSnapshotsStayDeliveredAcrossRestartUntilCountsChange()
    {
        using var directory = new TemporaryDirectory();
        AnalyticsAccount.Update(directory.RootPath, Guid.NewGuid().ToString("D"));
        new AnalyticsSettingsStore(directory.RootPath).SetDetailedTrackingEnabled(true);
        var usage = new LocalCaptureUsage(directory.RootPath);
        var created = DateTimeOffset.UtcNow.AddMinutes(-1);
        Assert.True(usage.Register("delivered", created, "sdk"));
        Assert.True(usage.RecordSavedEvidence("delivered", created, DateTimeOffset.UtcNow));
        usage.Flush(true);
        using var handler = new RecordingHandler();
        using var client = new HttpClient(handler);
        Assert.True(await new PostHogDispatcher(directory.RootPath, client,
            "https://posthog.example", "public-project-key").FlushAsync(CancellationToken.None));
        var outbox = new EventOutbox(Path.Combine(directory.RootPath, "analytics"));
        usage = new LocalCaptureUsage(directory.RootPath);
        usage.Flush(true);
        Assert.Empty(outbox.Load(250));

        Assert.True(usage.Register("next", created, "device"));
        Assert.True(usage.RecordSavedEvidence("next", created, DateTimeOffset.UtcNow));
        usage.Flush(true);
        var next = Assert.Single(outbox.Load(250));
        Assert.Equal("2", next.Envelope.Properties["capture_count"]!.ToString());
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task CaptureDeliveryRejectsSnapshotsFromAnEarlierConsentPeriod()
    {
        using var directory = new TemporaryDirectory();
        var account = Guid.NewGuid().ToString("D");
        AnalyticsAccount.Update(directory.RootPath, account);
        var settings = new AnalyticsSettingsStore(directory.RootPath);
        settings.SetDetailedTrackingEnabled(true);
        var oldConsent = settings.DetailedTrackingConsentId;
        settings.SetDetailedTrackingEnabled(false);
        settings.SetDetailedTrackingEnabled(true);
        var outbox = new EventOutbox(settings.AnalyticsDirectoryPath);
        foreach (var consent in new[] { oldConsent, settings.DetailedTrackingConsentId })
            outbox.TryStore(new EventEnvelope(consent!, LocalCaptureUsage.EventName, "cli_anon_device", EventLevel.Detailed,
                new Dictionary<string, object?> { ["account_id"] = account, ["consent_epoch"] = consent }, DateTimeOffset.UtcNow));
        using var handler = new RecordingHandler();
        using var client = new HttpClient(handler);
        Assert.True(await new PostHogDispatcher(directory.RootPath, client,
            "https://posthog.example", "public-project-key").FlushAsync(CancellationToken.None));
        var batch = Assert.Single(handler.Requests).Payload["batch"]!.AsArray();
        Assert.Equal(settings.DetailedTrackingConsentId, Assert.Single(batch)!["properties"]!["consent_epoch"]!.GetValue<string>());
    }

    [Fact]
    public async Task MissingBuildTokenKeepsAnonymousEventsForLaterDelivery()
    {
        using var directory = new TemporaryDirectory();
        var settings = new AnalyticsSettingsStore(directory.RootPath);
        var outbox = new EventOutbox(settings.AnalyticsDirectoryPath);
        outbox.TryStore(new EventEnvelope("local-device", "cli_daily_active", "cli_anon_device",
            EventLevel.Daily, new Dictionary<string, object?>(), DateTimeOffset.UtcNow));
        using var handler = new RecordingHandler();
        using var client = new HttpClient(handler);

        Assert.False(await new PostHogDispatcher(directory.RootPath, client,
            "https://posthog.example", string.Empty).FlushAsync(CancellationToken.None));

        Assert.Empty(handler.Requests);
        Assert.Single(outbox.Load(20));
    }

    [Fact]
    public async Task PublicHostDeliversLocalUsageWithoutCloudIdentity()
    {
        Assert.Equal(typeof(RuntimeCoordinator).Assembly, typeof(PostHogDispatcher).Assembly);
        using var directory = new TemporaryDirectory();
        var settings = new AnalyticsSettingsStore(directory.RootPath);
        var outbox = new EventOutbox(settings.AnalyticsDirectoryPath);
        outbox.TryStore(new EventEnvelope("local-device", "cli_daily_active", "cli_anon_device",
            EventLevel.Daily, new Dictionary<string, object?> { ["surface"] = "cli" }, DateTimeOffset.UtcNow));
        EngagementAnalytics.RecordAction(directory.RootPath, DateTimeOffset.UtcNow);
        EngagementAnalytics.RecordAction(directory.RootPath, DateTimeOffset.UtcNow);
        using var handler = new RecordingHandler();
        using var client = new HttpClient(handler);

        Assert.True(await new PostHogDispatcher(directory.RootPath, client,
            "https://posthog.example", "public-project-key").FlushAsync(CancellationToken.None));

        var request = Assert.Single(handler.Requests);
        Assert.Null(request.Authorization);
        Assert.Equal("https://posthog.example/batch/", request.Url);
        var batch = request.Payload["batch"]!.AsArray();
        Assert.Contains(batch, item => item!["distinct_id"]!.GetValue<string>() == "cli_anon_device");
        Assert.Contains(batch, item => item!["event"]!.GetValue<string>() == "local_engaged_hourly");
        Assert.DoesNotContain(batch, item => item!["event"]!.GetValue<string>() == "product_usage_snapshot");
        Assert.Empty(outbox.Load(20));
    }

    [Fact]
    public async Task DeliveryRejectsDetailedEventsFromUnsignedOrOtherAccounts()
    {
        using var directory = new TemporaryDirectory();
        var account = Guid.NewGuid().ToString("D");
        AnalyticsAccount.Update(directory.RootPath, account);
        var settings = new AnalyticsSettingsStore(directory.RootPath);
        settings.SetDetailedTrackingEnabled(true);
        var outbox = new EventOutbox(settings.AnalyticsDirectoryPath);
        foreach (var (id, eventAccount) in new[]
                 {
                     ("allowed", account), ("unsigned", (string?)null), ("other", Guid.NewGuid().ToString("D"))
                 })
            outbox.TryStore(new EventEnvelope(id, "cli_command_completed", "cli_anon_device",
                EventLevel.Detailed, new Dictionary<string, object?> { ["account_id"] = eventAccount },
                DateTimeOffset.UtcNow));
        using var handler = new RecordingHandler();
        using var client = new HttpClient(handler);

        Assert.True(await new PostHogDispatcher(directory.RootPath, client,
            "https://posthog.example", "public-project-key").FlushAsync(CancellationToken.None));

        var batch = Assert.Single(handler.Requests).Payload["batch"]!.AsArray();
        Assert.Equal("allowed", Assert.Single(batch)!["properties"]!["$insert_id"]!.GetValue<string>());
        Assert.Empty(outbox.Load(20));
    }

    [Fact]
    public async Task FailedDeliveryKeepsTheEventForTheNextHostRun()
    {
        using var directory = new TemporaryDirectory();
        var outbox = new EventOutbox(new AnalyticsSettingsStore(directory.RootPath).AnalyticsDirectoryPath);
        outbox.TryStore(new EventEnvelope("retry", "cli_daily_active", "cli_anon_device",
            EventLevel.Daily, new Dictionary<string, object?>(), DateTimeOffset.UtcNow));
        using var handler = new RecordingHandler { StatusCode = HttpStatusCode.ServiceUnavailable };
        using var client = new HttpClient(handler);

        Assert.False(await new PostHogDispatcher(directory.RootPath, client,
            "https://posthog.example", "public-project-key").FlushAsync(CancellationToken.None));
        Assert.Single(outbox.Load(20));

        handler.StatusCode = HttpStatusCode.OK;
        Assert.True(await new PostHogDispatcher(directory.RootPath, client,
            "https://posthog.example", "public-project-key").FlushAsync(CancellationToken.None));
        Assert.Empty(outbox.Load(20));
    }

    [Fact]
    public async Task OldIdentityEventsAreDiscardedBeforeDelivery()
    {
        using var directory = new TemporaryDirectory();
        var outbox = new EventOutbox(new AnalyticsSettingsStore(directory.RootPath).AnalyticsDirectoryPath);
        outbox.TryStore(new EventEnvelope("legacy", "$identify", "cli_anon_device",
            EventLevel.Daily, new Dictionary<string, object?>(), DateTimeOffset.UtcNow));
        using var handler = new RecordingHandler();
        using var client = new HttpClient(handler);

        Assert.True(await new PostHogDispatcher(directory.RootPath, client,
            "https://posthog.example", "public-project-key").FlushAsync(CancellationToken.None));
        Assert.Empty(handler.Requests);
        Assert.Empty(outbox.Load(20));
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<RecordedRequest> Requests { get; } = [];

        public HttpStatusCode StatusCode { get; set; } = HttpStatusCode.OK;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            Requests.Add(new RecordedRequest(request.RequestUri!.ToString(),
                request.Headers.Authorization?.ToString(), JsonNode.Parse(body)!.AsObject()));
            return new HttpResponseMessage(StatusCode)
            {
                Content = new StringContent("{\"status\":\"Ok\"}")
            };
        }
    }

    private sealed record RecordedRequest(string Url, string? Authorization, JsonObject Payload);
}
