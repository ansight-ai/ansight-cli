using Ansight.Host.Trends;

namespace Ansight.Host.Tests.Unit.Trends;

public sealed class WorkspaceTrendsEvaluatorTests
{
    [Fact]
    public void Evaluate_ResolvesObservationSpanAndChecksFrameRateWithinIt()
    {
        var startedUtc = DateTimeOffset.Parse("2026-08-20T01:00:00Z");
        var snapshot = CreateSnapshot(
            startedUtc,
            new SessionMetricChannel
            {
                ChannelId = 3,
                Name = "FPS",
                ColorHex = "#00ff00",
                Unit = "fps",
                Type = "frames"
            },
            Enumerable.Range(0, 9)
                .Select(index => new SessionMetricSample
                {
                    ChannelId = 3,
                    Value = index == 2 ? 45 : 60,
                    CapturedAtUtc = startedUtc.AddMilliseconds(index * 500)
                })
                .ToArray());
        var trends = new WorkspaceTrendsDefinition(
            "login-trends",
            snapshot.AppId,
            CreateSpan(),
            true,
            "fail",
            [
                new WorkspaceTrendsMetricDefinition(
                    "fps-p10",
                    new WorkspaceTrendsMetricSelector("fps", null, null, null, true),
                    "p10",
                    new WorkspaceTrendsBudget(55, null, null),
                    null,
                    5,
                    TimeSpan.FromSeconds(1),
                    TimeSpan.Zero,
                    TimeSpan.Zero),
                new WorkspaceTrendsMetricDefinition(
                    "low-fps-ratio",
                    new WorkspaceTrendsMetricSelector("fps", null, null, null, true),
                    "timeBelowRatio",
                    new WorkspaceTrendsBudget(null, 0.2, null),
                    50,
                    5,
                    TimeSpan.FromSeconds(1),
                    TimeSpan.Zero,
                    TimeSpan.Zero)
            ],
            "trends-hash",
            "/trends.json");

        var result = new WorkspaceTrendsEvaluator().Evaluate(snapshot, trends);

        Assert.Equal(WorkspaceTrendsStatus.Passed, result.Status);
        Assert.Equal(2, result.Metrics.Count);
        Assert.All(result.Metrics, measurement => Assert.Equal(WorkspaceTrendsStatus.Passed, measurement.Status));
        Assert.Equal(57d, result.Metrics[0].Value!.Value, precision: 3);
        Assert.Equal(0.125d, result.Metrics[1].Value!.Value, precision: 3);
    }

    [Fact]
    public void Evaluate_MemoryTailMeasuresRetainedGrowthAndFlattening()
    {
        const long mebibyte = 1024L * 1024L;
        var startedUtc = DateTimeOffset.Parse("2026-08-20T01:00:00Z");
        var samples = new[]
        {
            Sample(1, 100 * mebibyte, startedUtc.AddSeconds(-2)),
            Sample(1, 100 * mebibyte, startedUtc.AddSeconds(-1)),
            Sample(1, 110 * mebibyte, startedUtc),
            Sample(1, 140 * mebibyte, startedUtc.AddSeconds(2)),
            Sample(1, 160 * mebibyte, startedUtc.AddSeconds(3)),
            Sample(1, 125 * mebibyte, startedUtc.AddSeconds(4)),
            Sample(1, 125 * mebibyte, startedUtc.AddSeconds(5)),
            Sample(1, 126 * mebibyte, startedUtc.AddSeconds(6)),
            Sample(1, 126 * mebibyte, startedUtc.AddSeconds(7)),
            Sample(1, 127 * mebibyte, startedUtc.AddSeconds(8))
        };
        var snapshot = CreateSnapshot(
            startedUtc,
            new SessionMetricChannel
            {
                ChannelId = 1,
                Name = "Physical Footprint",
                ColorHex = "#ff00ff",
                Unit = "bytes",
                Type = "memory"
            },
            samples);
        var trends = new WorkspaceTrendsDefinition(
            "guide-memory",
            snapshot.AppId,
            CreateSpan(),
            true,
            "fail",
            [
                new WorkspaceTrendsMetricDefinition(
                    "retained",
                    new WorkspaceTrendsMetricSelector("memory", null, null, null, true),
                    "memoryRetainedIncreaseMiB",
                    new WorkspaceTrendsBudget(null, 30, null),
                    null,
                    2,
                    TimeSpan.FromSeconds(2),
                    TimeSpan.FromSeconds(2),
                    TimeSpan.FromSeconds(4)),
                new WorkspaceTrendsMetricDefinition(
                    "tail-slope",
                    new WorkspaceTrendsMetricSelector("memory", null, null, null, true),
                    "tailSlopeMiBPerSecond",
                    new WorkspaceTrendsBudget(null, null, 1),
                    null,
                    2,
                    TimeSpan.FromSeconds(2),
                    TimeSpan.Zero,
                    TimeSpan.FromSeconds(4))
            ],
            "trends-hash",
            "/trends.json");

        var result = new WorkspaceTrendsEvaluator().Evaluate(snapshot, trends);

        Assert.Equal(WorkspaceTrendsStatus.Passed, result.Status);
        Assert.InRange(result.Metrics[0].Value!.Value, 25, 27);
        Assert.InRange(result.Metrics[1].Value!.Value, 0, 1);
    }

    [Fact]
    public void Evaluate_SkipsMetricsForOtherPlatforms()
    {
        var startedUtc = DateTimeOffset.Parse("2026-08-20T01:00:00Z");
        var snapshot = CreateSnapshot(
            startedUtc,
            new SessionMetricChannel
            {
                ChannelId = 1,
                Name = "Physical Footprint",
                ColorHex = "#ff00ff",
                Unit = "bytes",
                Type = "memory"
            },
            Enumerable.Range(0, 9)
                .Select(index => Sample(1, 100 + index, startedUtc.AddMilliseconds(index * 500)))
                .ToArray());
        var iosMeasurement = new WorkspaceTrendsMetricDefinition(
            "ios-memory",
            new WorkspaceTrendsMetricSelector(null, "Physical Footprint", null, null, true),
            "max",
            new WorkspaceTrendsBudget(null, 200, null),
            null,
            5,
            TimeSpan.FromSeconds(1),
            TimeSpan.Zero,
            TimeSpan.Zero)
        {
            Platform = "ios"
        };
        var androidMeasurement = iosMeasurement with
        {
            MetricId = "android-memory",
            Platform = "android"
        };
        var trends = new WorkspaceTrendsDefinition(
            "guide-memory",
            snapshot.AppId,
            CreateSpan(),
            true,
            "fail",
            [iosMeasurement, androidMeasurement],
            "trends-hash",
            "/trends.json");

        var result = new WorkspaceTrendsEvaluator().Evaluate(snapshot, trends);

        Assert.Equal(WorkspaceTrendsStatus.Passed, result.Status);
        var measurement = Assert.Single(result.Metrics);
        Assert.Equal("ios-memory", measurement.MetricId);
    }

    private static WorkspaceTrendsSpanDefinition CreateSpan()
        => new(
            new WorkspaceEventAnchor("operation.started"),
            new WorkspaceEventAnchor("operation.completed"),
            WorkspaceTrendsSpanSelection.ExactlyOne,
            TimeSpan.FromSeconds(30));

    private static AppSessionSnapshot CreateSnapshot(
        DateTimeOffset startedUtc,
        SessionMetricChannel channel,
        IReadOnlyList<SessionMetricSample> samples)
        => new()
        {
            SessionId = "session-trends",
            AppId = "com.example.app",
            ClientName = "Test app",
            RemoteAddress = "local",
            CreatedUtc = startedUtc.AddSeconds(-5),
            ConfigId = null,
            Status = "connected",
            LastUpdatedUtc = startedUtc.AddSeconds(10),
            IsHistorical = false,
            DeviceProfile = new DeviceAppProfile
            {
                Device = new DeviceProfile
                {
                    OsName = "iOS"
                }
            },
            ApplicationEvents =
            [
                new SessionApplicationEvent("start", "operation.started", "operation", string.Empty, startedUtc, 0),
                new SessionApplicationEvent("end", "operation.completed", "operation", string.Empty, startedUtc.AddSeconds(4), 0)
            ],
            MetricChannels = [channel],
            Metrics = samples
        };

    private static SessionMetricSample Sample(byte channelId, long value, DateTimeOffset capturedAtUtc)
        => new()
        {
            ChannelId = channelId,
            Value = value,
            CapturedAtUtc = capturedAtUtc
        };
}
