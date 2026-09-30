namespace Ansight.Host.Tests.Unit.Metrics;

public sealed class HostMemorySpikeTelemetryAnalysisTests
{
    [Fact]
    public void AnalyzeMemorySpikes_SydneyMetroTreesPhysicalFootprintDetectsAnnotatedModerateRises()
    {
        var chart = CreateState(CreateSydneyMetroTreesModerateRiseSamples());

        var windows = MemorySpikeDetector.AnalyzeMemorySpikes(chart);

        Assert.Collection(
            windows,
            first =>
            {
                Assert.Equal(Timestamp("2026-06-05T00:07:45.834273+00:00"), first.Baseline.CapturedAtUtc);
                Assert.Equal(Timestamp("2026-06-05T00:07:46.239096+00:00"), first.StartUtc);
                Assert.Equal(Timestamp("2026-06-05T00:07:57.543108+00:00"), first.Peak.CapturedAtUtc);
                Assert.Equal(143704064, first.DeltaBytes);
            },
            second =>
            {
                Assert.Equal(Timestamp("2026-06-05T00:13:10.906685+00:00"), second.Baseline.CapturedAtUtc);
                Assert.Equal(Timestamp("2026-06-05T00:13:11.310365+00:00"), second.StartUtc);
                Assert.Equal(Timestamp("2026-06-05T00:13:22.216134+00:00"), second.Peak.CapturedAtUtc);
                Assert.Equal(139771904, second.DeltaBytes);
            });
    }

    [Fact]
    public void AnalyzeMemorySpikes_SydneyMetroTreesPhysicalFootprintHonorsConfiguredThresholds()
    {
        var chart = CreateState(CreateSydneyMetroTreesModerateRiseSamples());

        var stricterRatio = MemorySpikeDetector.AnalyzeMemorySpikes(
            chart,
            MemorySpikeTelemetryAnalysisOptions.FromPreferenceValues(20, 128));
        var stricterAbsoluteFloor = MemorySpikeDetector.AnalyzeMemorySpikes(
            chart,
            MemorySpikeTelemetryAnalysisOptions.FromPreferenceValues(15, 256));

        Assert.Empty(stricterRatio);
        Assert.Empty(stricterAbsoluteFloor);
    }

    [Fact]
    public void AnalyzeMemorySpikes_RedPointPhysicalFootprintKeepsRiseWindowLocal()
    {
        var chart = CreateState(CreateRedPointPhysicalFootprintSamples());

        var windows = MemorySpikeDetector.AnalyzeMemorySpikes(chart);

        var target = Assert.Single(
            windows,
            window => window.Peak.CapturedAtUtc == Timestamp("2026-05-21T12:12:13.438327+00:00"));
        Assert.Equal(Timestamp("2026-05-21T12:12:06.169766+00:00"), target.Baseline.CapturedAtUtc);
        Assert.Equal(Timestamp("2026-05-21T12:12:06.571855+00:00"), target.StartUtc);
        Assert.Equal(Timestamp("2026-05-21T12:12:14.438327+00:00"), target.EndUtc);
        Assert.Equal(274317600, target.DeltaBytes);
        Assert.DoesNotContain(windows, window =>
            window.StartUtc <= Timestamp("2026-05-21T12:12:03+00:00")
            && window.EndUtc >= Timestamp("2026-05-21T12:12:19+00:00"));
    }

    [Fact]
    public void AnalyzeMemorySpikes_IgnoresNonMemoryMetricChannels()
    {
        var startUtc = Timestamp("2026-06-05T00:00:00+00:00");
        var metrics = new[]
        {
            new SessionMetricSample { ChannelId = 5, Value = 100, CapturedAtUtc = startUtc },
            new SessionMetricSample { ChannelId = 5, Value = 512 * 1024 * 1024, CapturedAtUtc = startUtc.AddSeconds(1) }
        };
        var chart = new MetricChartState(
            metrics,
            new Dictionary<byte, SessionMetricChannel>
            {
                [5] = new SessionMetricChannel
                {
                    ChannelId = 5,
                    Name = "Battery Level",
                    ColorHex = "#FFCC00",
                    Unit = "percent",
                    Type = "battery"
                }
            },
            metrics[0].CapturedAtUtc,
            metrics[^1].CapturedAtUtc,
            metrics[^1].CapturedAtUtc,
            false);

        var windows = MemorySpikeDetector.AnalyzeMemorySpikes(chart);

        Assert.Empty(windows);
    }

    private static MetricChartState CreateState(IReadOnlyList<SessionMetricSample> metrics)
    {
        return new MetricChartState(
            metrics,
            new Dictionary<byte, SessionMetricChannel>
            {
                [1] = new SessionMetricChannel
                {
                    ChannelId = 1,
                    Name = "Physical Footprint",
                    ColorHex = "#007AFF"
                }
            },
            metrics[0].CapturedAtUtc,
            metrics[^1].CapturedAtUtc,
            metrics[^1].CapturedAtUtc,
            false);
    }

    private static IReadOnlyList<SessionMetricSample> CreateSydneyMetroTreesModerateRiseSamples()
    {
        return new[]
        {
            Sample("2026-06-05T00:07:44.622259+00:00", 771149280),
            Sample("2026-06-05T00:07:45.430174+00:00", 772296160),
            Sample("2026-06-05T00:07:45.834273+00:00", 781536736),
            Sample("2026-06-05T00:07:46.239096+00:00", 804589024),
            Sample("2026-06-05T00:07:50.274157+00:00", 836373984),
            Sample("2026-06-05T00:07:51.483683+00:00", 873074144),
            Sample("2026-06-05T00:07:57.543108+00:00", 925240800),
            Sample("2026-06-05T00:13:10.099393+00:00", 845123040),
            Sample("2026-06-05T00:13:10.906685+00:00", 845139424),
            Sample("2026-06-05T00:13:11.310365+00:00", 870239712),
            Sample("2026-06-05T00:13:11.713104+00:00", 894422496),
            Sample("2026-06-05T00:13:12.116862+00:00", 902958560),
            Sample("2026-06-05T00:13:21.812145+00:00", 959630816),
            Sample("2026-06-05T00:13:22.216134+00:00", 984911328)
        };
    }

    private static IReadOnlyList<SessionMetricSample> CreateRedPointPhysicalFootprintSamples()
    {
        return new[]
        {
            Sample("2026-05-21T12:11:47.157140+00:00", 139904184),
            Sample("2026-05-21T12:11:47.559642+00:00", 390415664),
            Sample("2026-05-21T12:11:47.961838+00:00", 443909448),
            Sample("2026-05-21T12:11:48.365606+00:00", 475661640),
            Sample("2026-05-21T12:11:48.770065+00:00", 477693256),
            Sample("2026-05-21T12:11:49.175236+00:00", 477971784),
            Sample("2026-05-21T12:11:49.580120+00:00", 477988168),
            Sample("2026-05-21T12:11:49.986074+00:00", 509658440),
            Sample("2026-05-21T12:11:50.391052+00:00", 446825800),
            Sample("2026-05-21T12:11:50.795746+00:00", 442123592),
            Sample("2026-05-21T12:11:51.201576+00:00", 442303816),
            Sample("2026-05-21T12:11:51.605484+00:00", 442123592),
            Sample("2026-05-21T12:11:52.008407+00:00", 473417032),
            Sample("2026-05-21T12:11:52.410390+00:00", 447006048),
            Sample("2026-05-21T12:11:52.815390+00:00", 445007200),
            Sample("2026-05-21T12:11:53.221214+00:00", 443876704),
            Sample("2026-05-21T12:11:53.626617+00:00", 443811168),
            Sample("2026-05-21T12:11:54.031641+00:00", 443811168),
            Sample("2026-05-21T12:11:54.436544+00:00", 443811168),
            Sample("2026-05-21T12:11:54.840261+00:00", 443811168),
            Sample("2026-05-21T12:11:55.245231+00:00", 475186528),
            Sample("2026-05-21T12:11:55.648404+00:00", 443811168),
            Sample("2026-05-21T12:11:56.052859+00:00", 443811168),
            Sample("2026-05-21T12:11:56.456457+00:00", 443811168),
            Sample("2026-05-21T12:11:56.861076+00:00", 443876704),
            Sample("2026-05-21T12:11:57.264688+00:00", 476710240),
            Sample("2026-05-21T12:11:57.671075+00:00", 482379104),
            Sample("2026-05-21T12:11:58.074630+00:00", 482182496),
            Sample("2026-05-21T12:11:58.477679+00:00", 496944480),
            Sample("2026-05-21T12:11:58.885901+00:00", 516310368),
            Sample("2026-05-21T12:11:59.290910+00:00", 516343136),
            Sample("2026-05-21T12:11:59.695383+00:00", 516736352),
            Sample("2026-05-21T12:12:00.100523+00:00", 516736352),
            Sample("2026-05-21T12:12:00.505302+00:00", 522765664),
            Sample("2026-05-21T12:12:00.909156+00:00", 517113184),
            Sample("2026-05-21T12:12:01.312219+00:00", 518653280),
            Sample("2026-05-21T12:12:01.719264+00:00", 518735200),
            Sample("2026-05-21T12:12:02.122592+00:00", 524813688),
            Sample("2026-05-21T12:12:02.525347+00:00", 550569336),
            Sample("2026-05-21T12:12:02.929908+00:00", 525370744),
            Sample("2026-05-21T12:12:03.338288+00:00", 525387128),
            Sample("2026-05-21T12:12:03.743316+00:00", 522913144),
            Sample("2026-05-21T12:12:04.148831+00:00", 522339704),
            Sample("2026-05-21T12:12:04.552002+00:00", 552813944),
            Sample("2026-05-21T12:12:04.955291+00:00", 534447480),
            Sample("2026-05-21T12:12:05.359522+00:00", 536642936),
            Sample("2026-05-21T12:12:05.765093+00:00", 535938424),
            Sample("2026-05-21T12:12:06.169766+00:00", 535692664),
            Sample("2026-05-21T12:12:06.571855+00:00", 570066320),
            Sample("2026-05-21T12:12:06.974464+00:00", 583321000),
            Sample("2026-05-21T12:12:07.379213+00:00", 587859368),
            Sample("2026-05-21T12:12:07.782552+00:00", 598279616),
            Sample("2026-05-21T12:12:08.187800+00:00", 599901632),
            Sample("2026-05-21T12:12:08.590172+00:00", 610960856),
            Sample("2026-05-21T12:12:08.995120+00:00", 613369328),
            Sample("2026-05-21T12:12:09.397816+00:00", 658834928),
            Sample("2026-05-21T12:12:09.800812+00:00", 716015112),
            Sample("2026-05-21T12:12:10.205910+00:00", 674235936),
            Sample("2026-05-21T12:12:10.608283+00:00", 682411576),
            Sample("2026-05-21T12:12:11.012476+00:00", 690718264),
            Sample("2026-05-21T12:12:11.415193+00:00", 708560440),
            Sample("2026-05-21T12:12:11.820254+00:00", 729957944),
            Sample("2026-05-21T12:12:12.225109+00:00", 767362664),
            Sample("2026-05-21T12:12:12.631152+00:00", 738084456),
            Sample("2026-05-21T12:12:13.036130+00:00", 699713128),
            Sample("2026-05-21T12:12:13.438327+00:00", 810010264),
            Sample("2026-05-21T12:12:13.841967+00:00", 804947632),
            Sample("2026-05-21T12:12:14.245557+00:00", 806569648),
            Sample("2026-05-21T12:12:14.648978+00:00", 805586608),
            Sample("2026-05-21T12:12:15.055896+00:00", 810845872),
            Sample("2026-05-21T12:12:15.461511+00:00", 804816560),
            Sample("2026-05-21T12:12:15.866243+00:00", 804570800),
            Sample("2026-05-21T12:12:16.273530+00:00", 804587184),
            Sample("2026-05-21T12:12:16.679273+00:00", 771950256),
            Sample("2026-05-21T12:12:17.084874+00:00", 803342000),
            Sample("2026-05-21T12:12:17.487700+00:00", 777602736),
            Sample("2026-05-21T12:12:17.893739+00:00", 776177328),
            Sample("2026-05-21T12:12:18.299904+00:00", 776128176),
            Sample("2026-05-21T12:12:18.706146+00:00", 829245104),
            Sample("2026-05-21T12:12:19.112702+00:00", 854279856),
            Sample("2026-05-21T12:12:19.519600+00:00", 806536880),
            Sample("2026-05-21T12:12:19.925837+00:00", 806553264),
            Sample("2026-05-21T12:12:20.332310+00:00", 812582576),
            Sample("2026-05-21T12:12:20.738062+00:00", 806553264),
            Sample("2026-05-21T12:12:21.145251+00:00", 806553264),
            Sample("2026-05-21T12:12:21.549204+00:00", 806553264),
            Sample("2026-05-21T12:12:21.952418+00:00", 829048496),
            Sample("2026-05-21T12:12:22.355598+00:00", 821806792),
            Sample("2026-05-21T12:12:22.758781+00:00", 768116424),
            Sample("2026-05-21T12:12:23.162094+00:00", 789743328),
            Sample("2026-05-21T12:12:23.568354+00:00", 790071032),
            Sample("2026-05-21T12:12:23.971537+00:00", 833292024),
            Sample("2026-05-21T12:12:24.375696+00:00", 938215160),
            Sample("2026-05-21T12:12:24.782031+00:00", 902383352),
            Sample("2026-05-21T12:12:25.188773+00:00", 900024056),
            Sample("2026-05-21T12:12:25.596791+00:00", 883525368),
            Sample("2026-05-21T12:12:26.004242+00:00", 860948216),
            Sample("2026-05-21T12:12:26.411753+00:00", 860948216),
            Sample("2026-05-21T12:12:26.818892+00:00", 860800760),
            Sample("2026-05-21T12:12:27.224177+00:00", 830588664),
            Sample("2026-05-21T12:12:27.629762+00:00", 834422520)
        };
    }

    private static SessionMetricSample Sample(string capturedAtUtc, long value)
    {
        return new SessionMetricSample
        {
            ChannelId = 1,
            CapturedAtUtc = Timestamp(capturedAtUtc),
            Value = value
        };
    }

    private static DateTimeOffset Timestamp(string value)
    {
        return DateTimeOffset.Parse(value).ToUniversalTime();
    }
}
