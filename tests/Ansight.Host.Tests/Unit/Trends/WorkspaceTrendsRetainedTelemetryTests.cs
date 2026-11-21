using Ansight.Host.Tests.TestSupport;

namespace Ansight.Host.Tests.Unit.Trends;

public sealed class WorkspaceTrendsRetainedTelemetryTests
{
    [Fact]
    public async Task FinalizedSessionIncludesMetricsBeforeTheLiveTail()
    {
        using var environment = new TestEnvironment();
        var composition = new MefHostComposition(environment.ApplicationPaths,
            new FileBackedEncryptedStorage(environment.SecureStorageFilePath));
        using var runtime = composition.Get<RuntimeCoordinator>();
        var state = composition.Get<IRuntimeState>();
        var directory = Path.Combine(environment.RootPath, "ansight", "trends");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "retained.json"), """
            {"schemaVersion":1,"id":"retained","appId":"test.electron",
             "span":{"start":{"event":{"label":"work.started"}},"end":{"event":{"label":"work.completed"}},"selection":"all"},
             "metrics":[{"id":"rss","channel":{"name":"RSS"},"statistic":"max","minimumSamples":1,"budget":{"lte":200}}]}
            """);
        Assert.True(runtime.Apps.Register(new AppRegistrationRequest("test.electron", "Electron", environment.RootPath)).IsSuccess);
        var id = state.CreateSession("test.electron", "Electron", IPAddress.Loopback, null, "process");
        var start = DateTimeOffset.UtcNow;
        state.UpdateSessionMetricChannels(id, [new SessionMetricChannel
        { ChannelId = 100, Name = "RSS", ColorHex = "#00ff00", Type = "memory", Unit = "MiB" }]);
        state.AddSessionApplicationEvents(id, [
            new SessionApplicationEvent("begin", "work.started", "operation", "", start, 0),
            new SessionApplicationEvent("end", "work.completed", "operation", "", start.AddSeconds(2), 0)]);
        state.AddSessionMetrics(id, [new SessionMetricSample
        { ChannelId = 100, Value = 125, CapturedAtUtc = start.AddSeconds(1) }], telemetrySegmentId: 1);
        state.AddSessionMetrics(id, [new SessionMetricSample
        { ChannelId = 100, Value = 999, CapturedAtUtc = start.AddMinutes(5) }], telemetrySegmentId: 1);
        state.SetSessionStatus(id, "WebSocket Closed");
        Assert.True(state.TryGetSessionLiveContentSnapshot(id, out var tail));
        Assert.DoesNotContain(tail!.Metrics, sample => sample.Value == 125);

        var report = await runtime.Trends.EvaluateRegisteredSessionAsync(id);
        Assert.NotNull(report);
        Assert.Equal(WorkspaceTrendsStatus.Passed, report.Status);
        Assert.Equal(125d, Assert.Single(Assert.Single(report.Checks).Metrics).Value);
    }
}
