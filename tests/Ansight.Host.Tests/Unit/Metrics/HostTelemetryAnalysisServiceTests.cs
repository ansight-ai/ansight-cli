using Ansight.Host;
using Ansight.Infrastructure.Preferences;

namespace Ansight.Host.Tests.Unit.Metrics;

public sealed class HostTelemetryAnalysisServiceTests
{
    private const long Megabyte = 1024L * 1024L;

    [Fact]
    public void Defaults_MatchHostPreferenceDefaults()
    {
        Assert.Equal(
            TelemetryAnalysisPreferenceDefaults.MemorySpikeMinimumIncreasePercent,
            MemorySpikeAnalysisOptions.DefaultMinimumIncreasePercent);
        Assert.Equal(
            TelemetryAnalysisPreferenceDefaults.MemorySpikeMinimumIncreaseMegabytes,
            MemorySpikeAnalysisOptions.DefaultMinimumIncreaseMegabytes);
    }

    [Fact]
    public void Analyze_ProducesNamedFpsAndMemoryEventsWithSharedSeverity()
    {
        var startUtc = DateTimeOffset.Parse("2026-08-19T01:00:00Z");
        var metrics = new[]
        {
            Sample(3, 60, startUtc),
            Sample(1, 100 * Megabyte, startUtc),
            Sample(3, 15, startUtc.AddSeconds(1)),
            Sample(1, 250 * Megabyte, startUtc.AddSeconds(2))
        };
        var channels = new[]
        {
            Channel(1, "Physical Footprint", "memory", "bytes"),
            Channel(3, "FPS", "frames", "fps")
        };
        var service = new TelemetryAnalysisService();

        var fpsDrop = Assert.Single(service.AnalyzeFpsDrops(metrics, channels));
        var memorySpike = Assert.Single(service.AnalyzeMemorySpikes(metrics, channels));

        Assert.Equal("FPS", fpsDrop.ChannelName);
        Assert.Equal(45L, fpsDrop.DropFps);
        Assert.Equal(TelemetryAnalysisSeverity.Critical, fpsDrop.Severity);
        Assert.Equal(startUtc.AddSeconds(1), fpsDrop.Minimum.CapturedAtUtc);

        Assert.Equal("Physical Footprint", memorySpike.ChannelName);
        Assert.Equal(150 * Megabyte, memorySpike.DeltaBytes);
        Assert.Equal(150d, memorySpike.DeltaPercent);
        Assert.Equal(TelemetryAnalysisSeverity.Major, memorySpike.Severity);
        Assert.Equal(startUtc.AddSeconds(2), memorySpike.Peak.CapturedAtUtc);
    }

    [Fact]
    public void AnalyzeMemorySpikes_HonorsPublicThresholdOptions()
    {
        var startUtc = DateTimeOffset.Parse("2026-08-19T01:00:00Z");
        var metrics = new[]
        {
            Sample(1, 100 * Megabyte, startUtc),
            Sample(1, 250 * Megabyte, startUtc.AddSeconds(1))
        };
        var channels = new[] { Channel(1, "Physical Footprint", "memory", "bytes") };
        var service = new TelemetryAnalysisService();

        var events = service.AnalyzeMemorySpikes(
            metrics,
            channels,
            new MemorySpikeAnalysisOptions(15, 256));

        Assert.Empty(events);
    }

    private static SessionMetricSample Sample(byte channelId, long value, DateTimeOffset capturedAtUtc)
    {
        return new SessionMetricSample
        {
            ChannelId = channelId,
            Value = value,
            CapturedAtUtc = capturedAtUtc
        };
    }

    private static SessionMetricChannel Channel(byte channelId, string name, string type, string unit)
    {
        return new SessionMetricChannel
        {
            ChannelId = channelId,
            Name = name,
            ColorHex = "#000000",
            Type = type,
            Unit = unit
        };
    }
}
