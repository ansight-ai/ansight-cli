using Ansight.Host;

namespace Ansight.Host.Tests.Unit.Runtime;

public sealed class HostReplayFrameCadenceAnalyzerTests
{
    [Fact]
    public void DenseFramesWithCoveredSteps_AreReliable()
    {
        var startUtc = DateTimeOffset.Parse("2026-08-23T01:00:00Z");
        var frames = Enumerable.Range(0, 7)
            .Select(index => CreateFrame(startUtc.AddSeconds(index), index))
            .ToArray();
        var steps = new[]
        {
            CreateStep(1, startUtc.AddSeconds(0.5)),
            CreateStep(2, startUtc.AddSeconds(2.5)),
            CreateStep(3, startUtc.AddSeconds(5.5))
        };

        var cadence = ReplayFrameCadenceAnalyzer.Analyze(
            frames,
            steps,
            startUtc,
            startUtc.AddSeconds(6));

        Assert.Equal("reliable", cadence.Rating);
        Assert.True(cadence.AllowsLiveReplay);
        Assert.Equal(7, cadence.FrameCount);
        Assert.Equal(1_000, cadence.MedianIntervalMilliseconds);
        Assert.Equal(1_000, cadence.P95IntervalMilliseconds);
        Assert.Equal(3, cadence.CoveredReplayStepCount);
        Assert.Equal(100, cadence.ReplayStepCoveragePercent);
        Assert.Equal(3, cadence.DistinctSupportingFrameCount);
        Assert.Equal(100, cadence.DistinctSupportingFramePercent);
    }

    [Fact]
    public void SharedNearestFrameWithDistinctNearbyAlternatives_IsReliable()
    {
        var startUtc = DateTimeOffset.Parse("2026-08-23T01:00:00Z");
        var frames = new[]
        {
            CreateFrame(startUtc, 0),
            CreateFrame(startUtc.AddSeconds(0.75), 1),
            CreateFrame(startUtc.AddSeconds(1.5), 2)
        };
        var steps = new[]
        {
            CreateStep(3, startUtc.AddSeconds(0.6)),
            CreateStep(1, startUtc.AddSeconds(0.4)),
            CreateStep(2, startUtc.AddSeconds(0.5))
        };

        var cadence = ReplayFrameCadenceAnalyzer.Analyze(
            frames,
            steps,
            startUtc,
            startUtc.AddSeconds(2));

        Assert.Equal("reliable", cadence.Rating);
        Assert.True(cadence.AllowsLiveReplay);
        Assert.Equal(100, cadence.ReplayStepCoveragePercent);
        Assert.Equal(3, cadence.DistinctSupportingFrameCount);
        Assert.Equal(100, cadence.DistinctSupportingFramePercent);
        Assert.Equal(250, cadence.MedianReplayStepFrameDistanceMilliseconds);
        Assert.Empty(cadence.Findings);
    }

    [Fact]
    public void DistinctNearbyAlternatives_DoNotCoverAnActionOutsideTheWindow()
    {
        var startUtc = DateTimeOffset.Parse("2026-08-23T01:00:00Z");
        var frames = Enumerable.Range(0, 7)
            .Select(index => CreateFrame(startUtc.AddSeconds(index), index))
            .ToArray();
        var steps = new[] { 0d, 0.1, 2, 3, 4, 5, 6, 10 }
            .Select((seconds, index) => CreateStep(index + 1, startUtc.AddSeconds(seconds)))
            .ToArray();

        var cadence = ReplayFrameCadenceAnalyzer.Analyze(
            frames,
            steps,
            startUtc,
            startUtc.AddSeconds(10));

        Assert.Equal("degraded", cadence.Rating);
        Assert.False(cadence.AllowsLiveReplay);
        Assert.Equal(7, cadence.CoveredReplayStepCount);
        Assert.Equal(87.5, cadence.ReplayStepCoveragePercent);
        Assert.Equal(7, cadence.DistinctSupportingFrameCount);
        Assert.Equal(87.5, cadence.DistinctSupportingFramePercent);
    }

    [Fact]
    public void FiveSecondCadenceAndLongGap_AreUnreliable()
    {
        var startUtc = DateTimeOffset.Parse("2026-08-23T01:00:00Z");
        var frames = new[]
        {
            CreateFrame(startUtc, 0),
            CreateFrame(startUtc.AddSeconds(5), 1),
            CreateFrame(startUtc.AddSeconds(16), 2),
            CreateFrame(startUtc.AddSeconds(20), 3)
        };
        var steps = new[]
        {
            CreateStep(1, startUtc.AddSeconds(1)),
            CreateStep(2, startUtc.AddSeconds(8)),
            CreateStep(3, startUtc.AddSeconds(15)),
            CreateStep(4, startUtc.AddSeconds(18))
        };

        var cadence = ReplayFrameCadenceAnalyzer.Analyze(
            frames,
            steps,
            startUtc,
            startUtc.AddSeconds(20));

        Assert.Equal("unreliable", cadence.Rating);
        Assert.False(cadence.AllowsLiveReplay);
        Assert.Equal(5_000, cadence.MedianIntervalMilliseconds);
        Assert.Equal(11_000, cadence.P95IntervalMilliseconds);
        Assert.Equal(11_000, cadence.MaximumGapMilliseconds);
        Assert.Equal(2, cadence.CoveredReplayStepCount);
        Assert.Equal(50, cadence.ReplayStepCoveragePercent);
        Assert.Contains(cadence.Findings, finding =>
            finding.Contains("nearby frame", StringComparison.Ordinal));
    }

    [Fact]
    public void LongQuietGapFromDuplicateSuppression_DoesNotDegradeCoveredSteps()
    {
        var startUtc = DateTimeOffset.Parse("2026-08-23T01:00:00Z");
        var frames = new[]
        {
            CreateFrame(startUtc, 0),
            CreateFrame(startUtc.AddSeconds(1), 1),
            CreateFrame(startUtc.AddSeconds(119), 2),
            CreateFrame(startUtc.AddSeconds(120), 3)
        };
        var steps = new[]
        {
            CreateStep(1, startUtc.AddSeconds(0.5)),
            CreateStep(2, startUtc.AddSeconds(119.5))
        };

        var cadence = ReplayFrameCadenceAnalyzer.Analyze(
            frames,
            steps,
            startUtc,
            startUtc.AddSeconds(120));

        Assert.Equal("reliable", cadence.Rating);
        Assert.True(cadence.AllowsLiveReplay);
        Assert.Equal(118_000, cadence.MaximumGapMilliseconds);
        Assert.Equal(2, cadence.DistinctSupportingFrameCount);
        Assert.Contains("exact consecutive duplicate images are suppressed", cadence.Message);
    }

    [Fact]
    public void ManyActionsSharingOneChangeFrame_AreUnreliable()
    {
        var startUtc = DateTimeOffset.Parse("2026-08-23T01:00:00Z");
        var frames = new[]
        {
            CreateFrame(startUtc, 0),
            CreateFrame(startUtc.AddSeconds(5), 1)
        };
        var steps = Enumerable.Range(1, 4)
            .Select(index => CreateStep(index, startUtc.AddSeconds(4 + index / 10d)))
            .ToArray();

        var cadence = ReplayFrameCadenceAnalyzer.Analyze(
            frames,
            steps,
            startUtc,
            startUtc.AddSeconds(5));

        Assert.Equal("unreliable", cadence.Rating);
        Assert.False(cadence.AllowsLiveReplay);
        Assert.Equal(4, cadence.CoveredReplayStepCount);
        Assert.Equal(100, cadence.ReplayStepCoveragePercent);
        Assert.Equal(1, cadence.DistinctSupportingFrameCount);
        Assert.Equal(25, cadence.DistinctSupportingFramePercent);
        Assert.Contains(cadence.Findings, finding =>
            finding.Contains("distinct supporting change frames", StringComparison.Ordinal));
    }

    [Fact]
    public void MissingFrames_AreUnreliable()
    {
        var startUtc = DateTimeOffset.Parse("2026-08-23T01:00:00Z");

        var cadence = ReplayFrameCadenceAnalyzer.Analyze(
            [],
            [CreateStep(1, startUtc.AddSeconds(1))],
            startUtc,
            startUtc.AddSeconds(3));

        Assert.Equal("unreliable", cadence.Rating);
        Assert.False(cadence.AllowsLiveReplay);
        Assert.Equal(0, cadence.FrameCount);
        Assert.Equal(3_000, cadence.MaximumGapMilliseconds);
        Assert.Equal(0, cadence.CoveredReplayStepCount);
    }

    private static SessionImageFrame CreateFrame(DateTimeOffset capturedAtUtc, int sequence)
        => new()
        {
            FrameId = $"frame-{sequence}",
            CapturedAtUtc = capturedAtUtc,
            Format = "jpeg",
            Width = 100,
            Height = 100,
            Quality = 80,
            ByteCount = 1
        };

    private static ReplayStep CreateStep(int sequence, DateTimeOffset capturedAtUtc)
        => new(sequence, "tap", $"Tap step {sequence}.", capturedAtUtc);
}
