using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ansight.Cli.Tests.Analytics;

public sealed class CliAnalyticsTests
{
    [Fact]
    public async Task DetailedTrackingCapturesOnlyNormalizedCommandData()
    {
        using var directory = TestDirectory.Create();
        EnableSignedInDetails(directory.Path);
        var analytics = new CliAnalytics(
            directory.Path,
            () => new DateTimeOffset(2026, 8, 21, 2, 0, 0, TimeSpan.Zero));

        await analytics.TrackCompletionAsync(
            CliArguments.Parse(["test", "run", "/private/workspace", "secret-test-id"]),
            CliExitCodes.Success,
            TimeSpan.FromSeconds(2));

        var events = ReadQueuedEvents(directory.Path)
            .Select(body => JsonNode.Parse(body)!.AsObject())
            .ToArray();
        Assert.Equal(4, events.Length);
        var detailed = Assert.Single(
            events,
            item => item["eventName"]!.GetValue<string>() == "cli_command_completed");
        Assert.Equal("test", detailed["properties"]!["command"]!.GetValue<string>());
        Assert.Equal("run", detailed["properties"]!["action"]!.GetValue<string>());
        Assert.Equal("tests", detailed["properties"]!["category"]!.GetValue<string>());
        Assert.Equal("local", detailed["properties"]!["feature_scope"]!.GetValue<string>());
        Assert.Equal("1s_5s", detailed["properties"]!["duration_bucket"]!.GetValue<string>());
        Assert.DoesNotContain("private/workspace", detailed.ToJsonString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret-test-id", detailed.ToJsonString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DetailedOptOutStillCapturesDailyUseAndPurgesQueuedDetails()
    {
        using var directory = TestDirectory.Create();
        var settings = new AnalyticsSettingsStore(directory.Path);
        settings.SetDetailedTrackingEnabled(false);
        var outbox = new EventOutbox(settings.AnalyticsDirectoryPath);
        outbox.TryStore(new EventEnvelope(
            "queued-detail",
            "cli_command_completed",
            "cli_anon_test",
            EventLevel.Detailed,
            new Dictionary<string, object?>(),
            DateTimeOffset.UtcNow));
        var analytics = new CliAnalytics(
            directory.Path,
            () => new DateTimeOffset(2026, 8, 21, 2, 0, 0, TimeSpan.Zero));

        await analytics.TrackCompletionAsync(
            CliArguments.Parse(["session", "show", "private-session-id"]),
            CliExitCodes.Success,
            TimeSpan.FromMilliseconds(50));

        var requests = ReadQueuedEvents(directory.Path);
        Assert.Equal(2, requests.Length);
        Assert.Contains(requests, body => body.Contains("cli_daily_active", StringComparison.Ordinal));
        Assert.Contains(requests, body => body.Contains("cli_command_used_daily", StringComparison.Ordinal));
        Assert.DoesNotContain(requests, body => body.Contains("cli_command_completed", StringComparison.Ordinal));
        Assert.Equal(2, outbox.Load(20).Count);
    }

    [Theory]
    [InlineData("session", "show")]
    [InlineData("device", "list")]
    [InlineData("ui", "snapshot")]
    public async Task LocalActivityIsMeasuredWithoutAnAccount(string command, string action)
    {
        using var directory = TestDirectory.Create();
        var analytics = new CliAnalytics(directory.Path);

        await analytics.TrackCompletionAsync(CliArguments.Parse([command, action, "private-id"]),
            CliExitCodes.Success, TimeSpan.FromMilliseconds(25));

        var events = ReadQueuedEvents(directory.Path)
            .Select(body => JsonNode.Parse(body)!.AsObject()).ToArray();
        var daily = Assert.Single(events, item => item["eventName"]!.GetValue<string>() == "cli_daily_active");
        Assert.Single(events, item => item["eventName"]!.GetValue<string>() == "cli_command_used_daily");
        Assert.StartsWith("cli_anon_", daily["distinctId"]!.GetValue<string>());
        Assert.DoesNotContain(events, item => item["eventName"]!.GetValue<string>() == "cli_command_completed");
        Assert.All(events, item =>
        {
            Assert.Null(item["properties"]!["command"]);
            Assert.Null(item["properties"]!["qualifying_command"]);
            Assert.Null(item["properties"]!["action"]);
        });
        Assert.DoesNotContain("private-id", string.Join("\n", events.Select(item => item.ToJsonString())), StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnsignedEngagementNeedsTwoActionsAndSendsAtMostOneEventPerHour()
    {
        using var directory = TestDirectory.Create();
        var now = new DateTimeOffset(2026, 8, 21, 2, 0, 0, TimeSpan.Zero);
        var analytics = new CliAnalytics(directory.Path, () => now);
        await analytics.TrackInvocationAsync(CliArguments.Parse(["device", "list"]));
        Assert.DoesNotContain(ReadQueuedEvents(directory.Path), body => body.Contains("local_engaged_hourly", StringComparison.Ordinal));

        await analytics.TrackInvocationAsync(CliArguments.Parse(["session", "list"]));
        await analytics.TrackInvocationAsync(CliArguments.Parse(["ui", "snapshot"]));
        var events = ReadQueuedEvents(directory.Path).Select(body => JsonNode.Parse(body)!.AsObject()).ToArray();
        Assert.Equal(3, events.Length);
        Assert.Single(events, item => item["eventName"]!.GetValue<string>() == "local_engaged_hourly");
        Assert.DoesNotContain("device", string.Join("\n", events.Select(item => item.ToJsonString())), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("session", string.Join("\n", events.Select(item => item.ToJsonString())), StringComparison.OrdinalIgnoreCase);

        now = now.AddHours(1);
        await analytics.TrackInvocationAsync(CliArguments.Parse(["device", "list"]));
        Assert.Single(ReadQueuedEvents(directory.Path), body => JsonNode.Parse(body)!["eventName"]!.GetValue<string>() == "local_engaged_hourly");
        await analytics.TrackInvocationAsync(CliArguments.Parse(["session", "list"]));
        Assert.Equal(2, ReadQueuedEvents(directory.Path).Count(body => JsonNode.Parse(body)!["eventName"]!.GetValue<string>() == "local_engaged_hourly"));
    }

    [Fact]
    public async Task DetailedConsentOnlyBecomesActiveAfterSignIn()
    {
        using var directory = TestDirectory.Create();
        var settings = new AnalyticsSettingsStore(directory.Path);
        Assert.False(settings.IsDetailedTrackingEnabled);
        Assert.Throws<InvalidOperationException>(() => settings.SetDetailedTrackingEnabled(true));
        Assert.False(settings.IsDetailedTrackingActive);
        var unsignedAnalytics = new CliAnalytics(directory.Path);
        await unsignedAnalytics.TrackInvocationAsync(CliArguments.Parse(["device", "list"]));
        Assert.DoesNotContain(ReadQueuedEvents(directory.Path), body => body.Contains("cli_command_completed", StringComparison.Ordinal));

        AnalyticsAccount.Update(directory.Path, Guid.NewGuid().ToString("D"));
        settings.SetDetailedTrackingEnabled(true);
        Assert.True(settings.IsDetailedTrackingActive);
        var signedInAnalytics = new CliAnalytics(directory.Path);
        await signedInAnalytics.TrackInvocationAsync(CliArguments.Parse(["session", "list"]));
        Assert.Contains(ReadQueuedEvents(directory.Path), body => body.Contains("cli_command_completed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CommandUseCadenceSurvivesDeliveryAndUtcMidnight()
    {
        using var directory = TestDirectory.Create();
        var now = new DateTimeOffset(2026, 8, 21, 23, 59, 0, TimeSpan.Zero);
        var analytics = new CliAnalytics(directory.Path, () => now);
        var outbox = new EventOutbox(new AnalyticsSettingsStore(directory.Path).AnalyticsDirectoryPath);

        await analytics.TrackInvocationAsync(CliArguments.Parse(["device", "list"]));
        Assert.Equal(2, outbox.Load(20).Count);
        foreach (var item in outbox.Load(20)) outbox.Remove(item.Path);

        now = now.AddMinutes(2);
        await analytics.TrackInvocationAsync(CliArguments.Parse(["session", "list"]));
        var afterMidnight = Assert.Single(outbox.Load(20));
        Assert.Equal("cli_daily_active", afterMidnight.Envelope.EventName);
        outbox.Remove(afterMidnight.Path);

        now = now.AddHours(24);
        await analytics.TrackInvocationAsync(CliArguments.Parse(["ui", "snapshot"]));
        Assert.Contains(outbox.Load(20), item => item.Envelope.EventName == "cli_command_used_daily");
    }

    [Fact]
    public async Task SignOutPurgesPendingDetailsButRetainsMandatoryEngagement()
    {
        using var directory = TestDirectory.Create();
        EnableSignedInDetails(directory.Path);
        var analytics = new CliAnalytics(directory.Path);
        await analytics.TrackInvocationAsync(CliArguments.Parse(["device", "list"]));
        var outbox = new EventOutbox(new AnalyticsSettingsStore(directory.Path).AnalyticsDirectoryPath);
        Assert.Contains(outbox.Load(20), item => item.Envelope.Level == EventLevel.Detailed);

        AnalyticsAccount.Update(directory.Path, null);

        Assert.All(outbox.Load(20), item => Assert.Equal(EventLevel.Daily, item.Envelope.Level));
        Assert.Contains(outbox.Load(20), item => item.Envelope.EventName == "cli_command_used_daily");
    }

    [Theory]
    [InlineData("share-batch")]
    [InlineData("summary")]
    [InlineData("share-url")]
    public async Task CloudSessionAliasesAreMeasuredAsCloudActivity(string action)
    {
        using var directory = TestDirectory.Create();
        EnableSignedInDetails(directory.Path);
        var analytics = new CliAnalytics(directory.Path);
        await analytics.TrackCompletionAsync(CliArguments.Parse(["session", action, "private-id"]),
            CliExitCodes.Success, TimeSpan.FromMilliseconds(25));

        var detailed = Assert.Single(ReadQueuedEvents(directory.Path)
            .Select(body => JsonNode.Parse(body)!.AsObject()),
            item => item["eventName"]!.GetValue<string>() == "cli_command_completed");
        Assert.Equal("cloud", detailed["properties"]!["feature_scope"]!.GetValue<string>());
    }

    [Fact]
    public async Task AnalyticsCommandPersistsDetailedOptOutAndReportsDailyTracking()
    {
        using var directory = TestDirectory.Create();
        var result = await RunAsync(
            ["analytics", "detailed", "disable", "--json", "--data-dir", directory.Path]);

        Assert.Equal(CliExitCodes.Success, result.ExitCode);
        var payload = JsonNode.Parse(result.StandardOutput)!.AsObject();
        Assert.True(payload["dailyUseTrackingEnabled"]!.GetValue<bool>());
        Assert.False(payload["detailedTrackingEnabled"]!.GetValue<bool>());
        Assert.False(new AnalyticsSettingsStore(directory.Path).IsDetailedTrackingEnabled);
    }

    [Fact]
    public async Task LifecycleTrackingCapturesInstallWithoutCountingDailyUse()
    {
        using var directory = TestDirectory.Create();
        var analytics = new CliAnalytics(
            directory.Path,
            () => new DateTimeOffset(2026, 8, 21, 2, 0, 0, TimeSpan.Zero));

        await analytics.TrackCompletionAsync(
            CliArguments.Parse(["analytics", "lifecycle", "install", "--channel", "public", "--rid", "osx-arm64"]),
            CliExitCodes.Success,
            TimeSpan.FromMilliseconds(10));

        var queued = ReadQueuedEvents(directory.Path).ToArray();
        Assert.Single(queued);
        var request = Assert.Single(queued, body => JsonNode.Parse(body)!["eventName"]!.GetValue<string>() == "cli_installed");
        Assert.DoesNotContain(queued, body => body.Contains("cli_daily_active", StringComparison.Ordinal));
        var payload = JsonNode.Parse(request)!.AsObject();
        Assert.Equal("cli_installed", payload["eventName"]!.GetValue<string>());
        Assert.Equal("public", payload["properties"]!["channel"]!.GetValue<string>());
        Assert.Equal("osx-arm64", payload["properties"]!["rid"]!.GetValue<string>());
        Assert.StartsWith("cli_anon_", payload["distinctId"]!.GetValue<string>());
        Assert.DoesNotContain("cli_daily_active", request, StringComparison.Ordinal);
        Assert.DoesNotContain("cli_command_completed", request, StringComparison.Ordinal);
    }

    private static async Task<CommandResult> RunAsync(string[] commandArguments)
    {
        using var standardOutput = new StringWriter();
        using var standardError = new StringWriter();
        var arguments = CliArguments.Parse(commandArguments);
        var exitCode = await CliApplication.RunParsedAsync(
            arguments,
            new CliOutput(arguments.IsJson, standardOutput, standardError),
            CancellationToken.None,
            accessAuthorizer: TestAccessAuthorizer.Allow,
            allowResidentHostForwarding: false);
        return new CommandResult(exitCode, standardOutput.ToString(), standardError.ToString());
    }

    private static string[] ReadQueuedEvents(string dataDirectory)
        => new EventOutbox(new AnalyticsSettingsStore(dataDirectory).AnalyticsDirectoryPath).Load(20)
            .Select(item => JsonSerializer.Serialize(item.Envelope, new JsonSerializerOptions(JsonSerializerDefaults.Web)))
            .ToArray();

    private static void EnableSignedInDetails(string dataDirectory)
    {
        AnalyticsAccount.Update(dataDirectory, Guid.NewGuid().ToString("D"));
        new AnalyticsSettingsStore(dataDirectory).SetDetailedTrackingEnabled(true);
    }
}
