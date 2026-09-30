using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ansight.Cli.Tests.Analytics;

public sealed class ProductUsageTests
{
    [Fact]
    public void NumericSnapshotsAccumulateAndDoNotReplayUnchangedCounters()
    {
        using var directory = TestDirectory.Create();
        EnableSignedInDetails(directory.Path);
        ProductUsage.Record(directory.Path, "capture", outcome: "succeeded", durationSeconds: 12, hasEvidence: true);
        ProductUsage.Record(directory.Path, "capture", outcome: "succeeded", durationSeconds: 18);
        ProductUsage.Flush(directory.Path, true);
        var first = Assert.Single(Entries(directory.Path));
        var p = Properties(first);
        Assert.Equal(2, p["host_capture_count"]!.GetValue<int>());
        Assert.Equal(30, p["host_capture_seconds"]!.GetValue<int>());
        Assert.Equal(18, p["host_capture_max_seconds"]!.GetValue<int>());
        Assert.Equal(1, p["host_capture_with_evidence"]!.GetValue<int>());
        ProductUsage.Flush(directory.Path, true);
        Assert.Single(Entries(directory.Path));
        ProductUsage.Record(directory.Path, "capture", outcome: "succeeded");
        ProductUsage.Flush(directory.Path, true);
        Assert.Equal(2, Entries(directory.Path).Count);
        Assert.Equal(3, Properties(Entries(directory.Path).Last())["host_capture_count"]!.GetValue<int>());
    }

    [Fact]
    public void UnknownCategoriesAndTamperedPropertiesNeverLeaveTheMachine()
    {
        using var directory = TestDirectory.Create();
        EnableSignedInDetails(directory.Path);
        ProductUsage.Record(directory.Path, "https://private.example/token");
        ProductUsage.Record(directory.Path, "capture", "secret-surface");
        ProductUsage.Record(directory.Path, "capture", outcome: "secret failure");
        Assert.Empty(Entries(directory.Path));
        ProductUsage.Record(directory.Path, "capture", durationSeconds: double.PositiveInfinity);
        var file = Assert.Single(Directory.GetFiles(Path.Combine(directory.Path, "analytics", "usage"), "*.json"));
        var state = JsonNode.Parse(File.ReadAllText(file))!;
        state["values"]!["private/path/token"] = 77;
        File.WriteAllText(file, state.ToJsonString());
        ProductUsage.Flush(directory.Path, true);
        var body = File.ReadAllText(Assert.Single(Entries(directory.Path)).Path);
        Assert.DoesNotContain("private/path/token", body);
        Assert.DoesNotContain("Infinity", body);
        Assert.DoesNotContain(directory.Path, body);
    }

    [Fact]
    public void DisablingPurgesPendingCountersAndDetailsAndReenableStartsFresh()
    {
        using var directory = TestDirectory.Create();
        EnableSignedInDetails(directory.Path);
        ProductUsage.Record(directory.Path, "logs", "local_web");
        ProductUsage.Flush(directory.Path, true);
        var settings = new AnalyticsSettingsStore(directory.Path);
        settings.SetDetailedTrackingEnabled(false);
        Assert.Empty(Entries(directory.Path));
        Assert.Empty(Directory.GetFiles(Path.Combine(directory.Path, "analytics", "usage"), "*.json"));
        ProductUsage.Record(directory.Path, "logs", "local_web");
        settings.SetDetailedTrackingEnabled(true);
        ProductUsage.Record(directory.Path, "logs", "local_web");
        ProductUsage.Flush(directory.Path, true);
        Assert.Equal(1, Properties(Assert.Single(Entries(directory.Path)))["local_web_logs_count"]!.GetValue<int>());
    }

    [Fact]
    public void MultipleTabsCountAnActiveMinuteOnce()
    {
        using var directory = TestDirectory.Create();
        EnableSignedInDetails(directory.Path);
        for (var n = 0; n < 10; n++) ProductUsage.Record(directory.Path, "active_minute", "local_web");
        ProductUsage.Flush(directory.Path, true);
        Assert.Equal(1, Properties(Assert.Single(Entries(directory.Path)))["local_web_active_minute_count"]!.GetValue<int>());
    }

    [Fact]
    public async Task ObserversPreserveResultsExceptionsAndCancellationEvenWithUnwritableStorage()
    {
        using var directory = TestDirectory.Create();
        var blocked = Path.Combine(directory.Path, "file"); File.WriteAllText(blocked, "not a directory");
        var value = new object();
        Assert.Same(value, await ProductUsage.ObserveAsync(blocked, "test", () => Task.FromResult(value), _ => "succeeded"));
        var failure = new InvalidOperationException("private failure text");
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => ProductUsage.ObserveAsync<object>(blocked, "test", () => Task.FromException<object>(failure), _ => "succeeded")));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ProductUsage.ObserveAsync<object>(blocked, "test", () => Task.FromCanceled<object>(new CancellationToken(true)), _ => "succeeded"));
        Assert.Same(value, ProductUsage.Observe(blocked, "task", () => value, _ => throw new Exception("analytics classifier failed")));
    }

    [Theory]
    [InlineData("app-graph", "run", "app-graph.run")]
    [InlineData("replay", "sentry", "replay.sentry.run")]
    [InlineData("audio", "inject", "audio.inject")]
    [InlineData("trends", "rebuild", "trends.rebuild")]
    [InlineData("auth", "signin", "auth.login")]
    [InlineData("auth", "signout", "auth.logout")]
    public void NewCommandGroupsHaveSafeOperationNames(string command, string action, string expected)
        => Assert.Equal(expected, CliAnalytics.ResolveOperation(CliArguments.Parse([command, action, "/private/customer/path"])));

    [Fact]
    public void NestedOperationDistinguishesActionsWithoutSendingIdentifiers()
    {
        Assert.Equal("profile.dotnet.cpu", CliAnalytics.ResolveOperation(CliArguments.Parse(["profile", "dotnet", "cpu", "secret-capture-id"])));
        Assert.Equal("profile.dotnet.start", CliAnalytics.ResolveOperation(CliArguments.Parse(["profile", "dotnet", "start", "/private/app"] )));
        Assert.Equal("session.show", CliAnalytics.ResolveOperation(CliArguments.Parse(["session", "show", "run"])));
    }

    [Fact]
    public void AccountSwitchDiscardsPriorDetailsAndRequiresNewConsent()
    {
        using var directory = TestDirectory.Create();
        var first = Guid.NewGuid().ToString("D");
        var second = Guid.NewGuid().ToString("D");
        var settings = new AnalyticsSettingsStore(directory.Path);
        ProductUsage.Record(directory.Path, "logs", "local_web");
        AnalyticsAccount.Update(directory.Path, first);
        settings.SetDetailedTrackingEnabled(true);
        ProductUsage.Record(directory.Path, "logs", "local_web");
        AnalyticsAccount.Update(directory.Path, second);
        Assert.False(settings.IsDetailedTrackingActive);
        ProductUsage.Record(directory.Path, "logs", "local_web");
        settings.SetDetailedTrackingEnabled(true);
        ProductUsage.Record(directory.Path, "logs", "local_web");
        ProductUsage.Flush(directory.Path, true);
        var snapshots = Entries(directory.Path).Select(Properties).ToArray();
        Assert.Single(snapshots);
        Assert.Single(snapshots, p => p["account_id"]?.GetValue<string>() == second);
        Assert.DoesNotContain(snapshots, p => p["account_id"]?.GetValue<string>() == first);
        Assert.All(snapshots, p => Assert.Equal(1, p["local_web_logs_count"]!.GetValue<int>()));
        Assert.Equal(second, AnalyticsAccount.Read(directory.Path));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RunningOperationDoesNotRecordAfterAccountSwitch(bool signedIn)
    {
        using var directory = TestDirectory.Create();
        var initial = signedIn ? Guid.NewGuid().ToString("D") : null;
        AnalyticsAccount.Update(directory.Path, initial);
        if (signedIn) new AnalyticsSettingsStore(directory.Path).SetDetailedTrackingEnabled(true);
        await ProductUsage.ObserveAsync(directory.Path, "test", async () =>
        {
            await Task.Yield();
            AnalyticsAccount.Update(directory.Path, Guid.NewGuid().ToString("D"));
            return true;
        }, _ => "succeeded");
        ProductUsage.Flush(directory.Path, true);
        Assert.Empty(Entries(directory.Path));
    }

    [Fact]
    public async Task CliCompletionSuppressesDetailsAfterAccountSwitch()
    {
        using var directory = TestDirectory.Create();
        var initial = Guid.NewGuid().ToString("D");
        AnalyticsAccount.Update(directory.Path, initial);
        new AnalyticsSettingsStore(directory.Path).SetDetailedTrackingEnabled(true);
        var cli = new CliAnalytics(directory.Path);
        AnalyticsAccount.Update(directory.Path, Guid.NewGuid().ToString("D"));
        await cli.TrackInvocationAsync(CliArguments.Parse(["session", "list"]));
        ProductUsage.Flush(directory.Path, true);
        var detailed = Entries(directory.Path).Where(e => e.Envelope.EventName is "cli_command_completed" or "product_usage_snapshot").ToArray();
        Assert.Empty(detailed);
        Assert.Contains(Entries(directory.Path), e => e.Envelope.EventName == "cli_daily_active");
    }

    [Fact]
    public void ManyActionsRemainInTheCurrentConsentedAccount()
    {
        using var directory = TestDirectory.Create();
        EnableSignedInDetails(directory.Path);
        for (var n = 0; n < 66; n++)
            ProductUsage.Record(directory.Path, "logs", "local_web");
        ProductUsage.Flush(directory.Path, true);
        Assert.Equal(66, Properties(Assert.Single(Entries(directory.Path)))["local_web_logs_count"]!.GetValue<int>());
    }

    private static IReadOnlyList<OutboxEntry> Entries(string path) => new EventOutbox(new AnalyticsSettingsStore(path).AnalyticsDirectoryPath).Load(100);
    private static JsonObject Properties(OutboxEntry entry) => JsonNode.Parse(File.ReadAllText(entry.Path))!["properties"]!.AsObject();

    private static void EnableSignedInDetails(string dataDirectory)
    {
        AnalyticsAccount.Update(dataDirectory, Guid.NewGuid().ToString("D"));
        new AnalyticsSettingsStore(dataDirectory).SetDetailedTrackingEnabled(true);
    }
}
