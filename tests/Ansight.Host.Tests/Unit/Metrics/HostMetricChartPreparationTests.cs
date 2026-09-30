namespace Ansight.Host.Tests.Unit.Metrics;

public sealed class HostMetricChartPreparationTests
{
    [Fact]
    public void Prepare_HistoricalStatePreservesVisibleOrderingAndSpikeSamples()
    {
        var startUtc = DateTimeOffset.Parse("2026-03-28T06:21:43Z");
        var metrics = new List<SessionMetricSample>();

        for (var index = 0; index < 240; index++)
        {
            var capturedAtUtc = startUtc.AddSeconds(index);
            metrics.Add(new SessionMetricSample
            {
                ChannelId = 1,
                Value = index == 120 ? 900 : 200 + index,
                CapturedAtUtc = capturedAtUtc
            });
            metrics.Add(new SessionMetricSample
            {
                ChannelId = 3,
                Value = index == 140 ? 12 : 60,
                CapturedAtUtc = capturedAtUtc
            });
        }

        metrics.Reverse();

        var state = CreateState(metrics, isLive: false);

        var prepared = MetricChartPreparation.Prepare(state, targetRenderSamplesPerSeries: 24);

        Assert.Equal(startUtc, prepared.EarliestVisibleUtc);
        Assert.Equal(startUtc.AddSeconds(239), prepared.LatestVisibleUtc);
        Assert.Equal(2, prepared.Series.Count);

        var memorySeries = Assert.Single(prepared.Series, series => !series.IsFpsChannel);
        Assert.Equal(240, memorySeries.VisibleSamples.Length);
        Assert.True(memorySeries.RenderSamples.Length <= 26);
        Assert.Equal(memorySeries.VisibleSamples[^1], memorySeries.RenderSamples[^1]);
        Assert.Contains(memorySeries.RenderSamples, sample => sample.Value == 900);
        Assert.True(IsChronologicallyOrdered(memorySeries.VisibleSamples));
        Assert.True(IsChronologicallyOrdered(memorySeries.RenderSamples));

        var fpsSeries = Assert.Single(prepared.Series, series => series.IsFpsChannel);
        Assert.Contains(fpsSeries.RenderSamples, sample => sample.Value == 12);
    }

    [Fact]
    public void Prepare_LiveStateKeepsOnlyLatestTwoMinutesVisible()
    {
        var startUtc = DateTimeOffset.Parse("2026-03-28T06:21:43Z");
        var metrics = new List<SessionMetricSample>();

        for (var index = 0; index <= 300; index++)
        {
            var capturedAtUtc = startUtc.AddSeconds(index);
            metrics.Add(new SessionMetricSample
            {
                ChannelId = 1,
                Value = 100 + index,
                CapturedAtUtc = capturedAtUtc
            });
            metrics.Add(new SessionMetricSample
            {
                ChannelId = 3,
                Value = 55,
                CapturedAtUtc = capturedAtUtc
            });
        }

        var state = CreateState(metrics, isLive: true);

        var prepared = MetricChartPreparation.Prepare(state, targetRenderSamplesPerSeries: 80);

        Assert.Equal(startUtc.AddMinutes(3), prepared.EarliestVisibleUtc);
        Assert.Equal(startUtc.AddMinutes(5), prepared.LatestVisibleUtc);
        Assert.All(prepared.Series, series =>
        {
            Assert.All(series.VisibleSamples, sample => Assert.True(sample.CapturedAtUtc >= prepared.EarliestVisibleUtc));
            Assert.Equal(121, series.VisibleSamples.Length);
        });
    }

    [Fact]
    public void Prepare_ReconnectTelemetryCreatesSeparateSegmentsPerChannel()
    {
        var startUtc = DateTimeOffset.Parse("2026-03-28T06:21:43Z");
        var metrics = new[]
        {
            new SessionMetricSample
            {
                ChannelId = 1,
                Value = 200,
                CapturedAtUtc = startUtc,
                SegmentId = 1
            },
            new SessionMetricSample
            {
                ChannelId = 1,
                Value = 210,
                CapturedAtUtc = startUtc.AddSeconds(1),
                SegmentId = 1
            },
            new SessionMetricSample
            {
                ChannelId = 1,
                Value = 260,
                CapturedAtUtc = startUtc.AddSeconds(9),
                SegmentId = 2
            },
            new SessionMetricSample
            {
                ChannelId = 1,
                Value = 270,
                CapturedAtUtc = startUtc.AddSeconds(10),
                SegmentId = 2
            },
            new SessionMetricSample
            {
                ChannelId = 3,
                Value = 60,
                CapturedAtUtc = startUtc,
                SegmentId = 1
            },
            new SessionMetricSample
            {
                ChannelId = 3,
                Value = 58,
                CapturedAtUtc = startUtc.AddSeconds(1),
                SegmentId = 1
            },
            new SessionMetricSample
            {
                ChannelId = 3,
                Value = 55,
                CapturedAtUtc = startUtc.AddSeconds(9),
                SegmentId = 2
            }
        };

        var prepared = MetricChartPreparation.Prepare(CreateState(metrics, isLive: false), targetRenderSamplesPerSeries: 16);

        var memorySeries = Assert.Single(prepared.Series, series => !series.IsFpsChannel);
        Assert.Equal(2, memorySeries.VisibleSegments.Count);
        Assert.Equal(startUtc.AddSeconds(1), memorySeries.VisibleSegments[0].Samples[^1].CapturedAtUtc);
        Assert.Equal(startUtc.AddSeconds(9), memorySeries.VisibleSegments[1].Samples[0].CapturedAtUtc);
        Assert.Equal(2, memorySeries.RenderSegments.Count);

        var fpsSeries = Assert.Single(prepared.Series, series => series.IsFpsChannel);
        Assert.Equal(2, fpsSeries.VisibleSegments.Count);
        Assert.Equal(2, fpsSeries.RenderSegments.Count);
    }

    [Fact]
    public void Prepare_FilteredChannelsOnlyReturnSelectedSeries()
    {
        var startUtc = DateTimeOffset.Parse("2026-03-28T06:21:43Z");
        var metrics = new[]
        {
            new SessionMetricSample
            {
                ChannelId = 1,
                Value = 200,
                CapturedAtUtc = startUtc
            },
            new SessionMetricSample
            {
                ChannelId = 1,
                Value = 220,
                CapturedAtUtc = startUtc.AddSeconds(1)
            },
            new SessionMetricSample
            {
                ChannelId = 3,
                Value = 60,
                CapturedAtUtc = startUtc
            },
            new SessionMetricSample
            {
                ChannelId = 3,
                Value = 58,
                CapturedAtUtc = startUtc.AddSeconds(1)
            }
        };

        var prepared = MetricChartPreparation.Prepare(
            CreateState(metrics, isLive: false),
            targetRenderSamplesPerSeries: 16,
            visibleChannelIds: new HashSet<byte> { 1 });

        var memorySeries = Assert.Single(prepared.Series);
        Assert.False(memorySeries.IsFpsChannel);
        Assert.Equal(1, memorySeries.ChannelId);
        Assert.True(prepared.HasMemoryMetrics);
        Assert.False(prepared.HasFpsMetrics);
        Assert.Equal(startUtc, prepared.EarliestVisibleUtc);
        Assert.Equal(startUtc.AddSeconds(1), prepared.LatestVisibleUtc);
    }

    [Fact]
    public void Prepare_WithNoVisibleChannelsReturnsEmptyState()
    {
        var startUtc = DateTimeOffset.Parse("2026-03-28T06:21:43Z");
        var metrics = new[]
        {
            new SessionMetricSample
            {
                ChannelId = 1,
                Value = 200,
                CapturedAtUtc = startUtc
            }
        };

        var prepared = MetricChartPreparation.Prepare(
            CreateState(metrics, isLive: false),
            targetRenderSamplesPerSeries: 16,
            visibleChannelIds: new HashSet<byte>());

        Assert.Same(MetricChartPreparedState.Empty, prepared);
    }

    private static MetricChartState CreateState(IReadOnlyList<SessionMetricSample> metrics, bool isLive)
    {
        return new MetricChartState(
            metrics,
            new Dictionary<byte, SessionMetricChannel>
            {
                [1] = new SessionMetricChannel
                {
                    ChannelId = 1,
                    Name = "Native heap",
                    ColorHex = "#007AFF"
                },
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
            isLive);
    }

    private static bool IsChronologicallyOrdered(IReadOnlyList<SessionMetricSample> samples)
    {
        for (var index = 1; index < samples.Count; index++)
        {
            if (samples[index - 1].CapturedAtUtc > samples[index].CapturedAtUtc)
            {
                return false;
            }
        }

        return true;
    }
}
