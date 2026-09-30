namespace Ansight.Host.Tests.Unit.Metrics;

public sealed class HostFpsDropTelemetryAnalysisTests
{
    [Fact]
    public void AnalyzeFpsDrops_UsesHighToLowBoundaryForSingleSampleDrop()
    {
        var startUtc = DateTimeOffset.Parse("2026-05-22T01:35:31.99574+00:00").ToUniversalTime();
        var chart = CreateState(
            Sample(startUtc, 55),
            Sample(startUtc.AddMilliseconds(1607), 3),
            Sample(startUtc.AddSeconds(8), 60));

        var windows = FpsDropDetector.AnalyzeFpsDrops(chart);

        var window = Assert.Single(windows);
        Assert.Equal(55, window.Baseline.Value);
        Assert.Equal(3, window.Minimum.Value);
        Assert.Equal(startUtc, window.StartUtc);
        Assert.Equal(startUtc.AddMilliseconds(1607), window.EndUtc);
    }

    [Fact]
    public void AnalyzeFpsDrops_IgnoresLowFpsRecovery()
    {
        var startUtc = DateTimeOffset.Parse("2026-05-22T01:35:31.99574+00:00").ToUniversalTime();
        var chart = CreateState(
            Sample(startUtc, 3),
            Sample(startUtc.AddMilliseconds(1607), 55),
            Sample(startUtc.AddSeconds(8), 60));

        var windows = FpsDropDetector.AnalyzeFpsDrops(chart);

        Assert.Empty(windows);
    }

    [Fact]
    public void AnalyzeFpsDrops_IgnoresLowToLowerFpsAfterGap()
    {
        var startUtc = DateTimeOffset.Parse("2026-05-22T01:35:31.99574+00:00").ToUniversalTime();
        var chart = CreateState(
            Sample(startUtc, 30),
            Sample(startUtc.AddSeconds(7), 5),
            Sample(startUtc.AddSeconds(8), 55));

        var windows = FpsDropDetector.AnalyzeFpsDrops(chart);

        Assert.Empty(windows);
    }

    [Fact]
    public void AnalyzeFpsDrops_ExtendsBoundaryToLowestSampleBeforeRecovery()
    {
        var startUtc = DateTimeOffset.Parse("2026-05-22T01:35:31.99574+00:00").ToUniversalTime();
        var chart = CreateState(
            Sample(startUtc, 60),
            Sample(startUtc.AddSeconds(1), 35),
            Sample(startUtc.AddSeconds(2), 25),
            Sample(startUtc.AddSeconds(3), 55));

        var windows = FpsDropDetector.AnalyzeFpsDrops(chart);

        var window = Assert.Single(windows);
        Assert.Equal(60, window.Baseline.Value);
        Assert.Equal(25, window.Minimum.Value);
        Assert.Equal(startUtc, window.StartUtc);
        Assert.Equal(startUtc.AddSeconds(2), window.EndUtc);
    }

    private static MetricChartState CreateState(params SessionMetricSample[] metrics)
    {
        return new MetricChartState(
            metrics,
            new Dictionary<byte, SessionMetricChannel>
            {
                [3] = new SessionMetricChannel
                {
                    ChannelId = 3,
                    Name = "FPS",
                    ColorHex = "#23B573"
                }
            },
            metrics.Min(sample => sample.CapturedAtUtc),
            metrics.Max(sample => sample.CapturedAtUtc),
            metrics.Max(sample => sample.CapturedAtUtc),
            false);
    }

    private static SessionMetricSample Sample(DateTimeOffset capturedAtUtc, long value)
    {
        return new SessionMetricSample
        {
            ChannelId = 3,
            CapturedAtUtc = capturedAtUtc,
            Value = value
        };
    }
}
