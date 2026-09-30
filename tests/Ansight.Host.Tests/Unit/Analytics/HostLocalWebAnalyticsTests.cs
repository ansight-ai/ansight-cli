using Ansight.Analytics;
using System.Text.Json;
using Ansight.Host.Tests.Unit.Runtime;

namespace Ansight.Host.Tests.Unit.Analytics;

public sealed class HostLocalWebAnalyticsTests
{
    [Fact]
    public async Task DailyAndDetailedEventsContainOnlyCoarseUsageProperties()
    {
        using var directory = new TemporaryDirectory();
        AnalyticsAccount.Update(directory.RootPath, Guid.NewGuid().ToString("D"));
        new AnalyticsSettingsStore(directory.RootPath).SetDetailedTrackingEnabled(true);
        var analytics = new LocalWebAnalytics(
            directory.RootPath,
            () => new DateTimeOffset(2026, 8, 21, 3, 0, 0, TimeSpan.Zero));

        await analytics.TrackAsync(new LocalWebAnalyticsRequest("daily_active"), true, CancellationToken.None);
        await analytics.TrackAsync(
            new LocalWebAnalyticsRequest("session_viewed", IsLive: true),
            true,
            CancellationToken.None);

        Assert.Equal(2, ReadQueuedEvents(directory.RootPath).Length);
        var events = ReadQueuedEvents(directory.RootPath).Select(body => JsonNode.Parse(body)!.AsObject()).ToArray();
        Assert.Single(events, item => item["eventName"]!.GetValue<string>() == "local_web_daily_active");
        var detailed = Assert.Single(events, item => item["eventName"]!.GetValue<string>() == "local_web_session_viewed");
        Assert.True(detailed["properties"]!["is_live"]!.GetValue<bool>());
        Assert.Equal("explorer", detailed["properties"]!["mode"]!.GetValue<string>());
        Assert.DoesNotContain("sessionId", ReadQueuedEvents(directory.RootPath)[1], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DetailedOptOutStillAllowsDailyEvent()
    {
        using var directory = new TemporaryDirectory();
        var analyticsDirectory = Path.Combine(directory.RootPath, "analytics");
        Directory.CreateDirectory(analyticsDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(analyticsDirectory, "settings.json"),
            """{"detailedTrackingEnabled":false}""");
        var analytics = new LocalWebAnalytics(
            directory.RootPath,
            () => new DateTimeOffset(2026, 8, 21, 3, 0, 0, TimeSpan.Zero));

        await analytics.TrackAsync(new LocalWebAnalyticsRequest("opened"), true, CancellationToken.None);
        await analytics.TrackAsync(new LocalWebAnalyticsRequest("daily_active"), true, CancellationToken.None);

        var request = Assert.Single(ReadQueuedEvents(directory.RootPath));
        Assert.Contains("local_web_daily_active", request, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("about")]
    [InlineData("account")]
    [InlineData("app_graph")]
    [InlineData("devices")]
    [InlineData("health")]
    [InlineData("session_admin")]
    [InlineData("settings")]
    [InlineData("test_execution")]
    [InlineData("test_history")]
    public async Task CurrentPanelsAreAccepted(string panel)
    {
        using var directory = new TemporaryDirectory();
        AnalyticsAccount.Update(directory.RootPath, Guid.NewGuid().ToString("D"));
        new AnalyticsSettingsStore(directory.RootPath).SetDetailedTrackingEnabled(true);
        var analytics = new LocalWebAnalytics(directory.RootPath);
        await analytics.TrackAsync(new LocalWebAnalyticsRequest("panel_opened", Panel: panel), true, CancellationToken.None);
        Assert.Contains(panel, Assert.Single(ReadQueuedEvents(directory.RootPath)));
    }

    [Fact]
    public async Task UnknownBrowserUsageCannotForgeHostCounters()
    {
        using var directory = new TemporaryDirectory();
        await new LocalWebAnalytics(directory.RootPath).TrackAsync(new LocalWebAnalyticsRequest("usage", Feature: "capture"), true, CancellationToken.None);
        ProductUsage.Flush(directory.RootPath, true);
        Assert.Empty(ReadQueuedEvents(directory.RootPath));
    }

    [Fact]
    public async Task TwoUnsignedInteractionsProduceOneHourlyEngagementEventWithoutDetails()
    {
        using var directory = new TemporaryDirectory();
        var now = new DateTimeOffset(2026, 8, 21, 3, 0, 0, TimeSpan.Zero);
        var analytics = new LocalWebAnalytics(directory.RootPath, () => now);

        await analytics.TrackAsync(new LocalWebAnalyticsRequest("engagement_action"), true, CancellationToken.None);
        Assert.Empty(ReadQueuedEvents(directory.RootPath));
        await analytics.TrackAsync(new LocalWebAnalyticsRequest("engagement_action"), true, CancellationToken.None);
        await analytics.TrackAsync(new LocalWebAnalyticsRequest("engagement_action"), true, CancellationToken.None);

        var eventBody = Assert.Single(ReadQueuedEvents(directory.RootPath));
        Assert.Contains("local_engaged_hourly", eventBody, StringComparison.Ordinal);
        Assert.DoesNotContain("command", eventBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("account_id", eventBody, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("api/sessions/secret-id/updates", "GET", null)]
    [InlineData("api/app-graph-recordings", "GET", null)]
    [InlineData("api/analytics/events", "POST", null)]
    [InlineData("api/sessions/secret-id/export", "GET", "export")]
    [InlineData("api/sessions/secret-id/files/content", "GET", "files")]
    public void PollsAndAnalyticsRequestsDoNotCountAsProductOperations(string route, string method, string? expected)
        => Assert.Equal(expected, LocalRequestUsage.Classify(route, method));

    private static string[] ReadQueuedEvents(string dataDirectory)
        => new EventOutbox(new AnalyticsSettingsStore(dataDirectory).AnalyticsDirectoryPath).Load(20)
            .Select(item => JsonSerializer.Serialize(item.Envelope, new JsonSerializerOptions(JsonSerializerDefaults.Web)))
            .ToArray();
}
