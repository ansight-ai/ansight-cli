namespace Ansight.Host.Tests.Unit.Runtime;

public sealed partial class HostTimelineTaskExtractorTests
{
    [Theory]
    [InlineData(500)]
    [InlineData(1500)]
    public void HostAndSdkLongPressEvidenceProducesOneActionWithRequestedDuration(int duration)
    {
        var start = DateTimeOffset.Parse("2026-08-18T00:00:00Z");
        var down = start.AddSeconds(1);
        var up = down.AddMilliseconds(duration);
        var snapshot = CreateSnapshot(start,
            [CreateTouch("down", down, 0.5, 0.5), CreateTouch("up", up, 0.5, 0.5)],
            [CreateVisualTree(down, "card", "Card")], hostEvents:
            [new HostInputAction("hold", "longPress", down, up.AddMilliseconds(40), "succeeded",
                NormalizedX: 0.5, NormalizedY: 0.5, DurationMilliseconds: duration).ToEvent()]);
        var result = TimelineTaskExtractor.Extract(snapshot, start, start.AddSeconds(4), "Hold card");
        Assert.Equal(1, result.GeneratedActionCount);
        Assert.Equal("longPress", Assert.Single(result.ReplaySteps).Kind);
        Assert.Contains($"\"durationMs\":{duration}", result.Source);
    }

    [Fact]
    public void ExtractLongPressPreservesSelectorAndHoldDurationInRunnableTaskAndReplay()
    {
        var start = DateTimeOffset.Parse("2026-08-18T00:00:00Z");
        var snapshot = CreateSnapshot(start,
            [CreateTouch("down", start.AddSeconds(1), 0.5, 0.5),
             CreateTouch("up", start.AddMilliseconds(2450), 0.5, 0.5)],
            [CreateVisualTree(start.AddSeconds(1), "card", "Card")]);
        var result = TimelineTaskExtractor.Extract(snapshot, start, start.AddSeconds(4), "Hold card");
        Assert.Equal(1, result.GeneratedActionCount);
        Assert.Empty(result.Diagnostics);
        Assert.Contains("ansight.ui.longPress", result.Source);
        Assert.Contains("\"automationId\":\"card\"", result.Source);
        Assert.Contains("\"durationMs\":1450", result.Source);
        Assert.Equal("longPress", Assert.Single(result.ReplaySteps).Kind);
        Assert.Contains("ansight_long_press_ui", result.ReplaySteps[0].Instruction);
        AssertTaskSourceLoads(result.Source, "hold-card");
    }

    [Fact]
    public void ExtractLongPressOutsideSupportedDurationDoesNotShortenIt()
    {
        var start = DateTimeOffset.Parse("2026-08-18T00:00:00Z");
        var snapshot = CreateSnapshot(start,
            [CreateTouch("down", start.AddSeconds(1), 0.5, 0.5),
             CreateTouch("up", start.AddSeconds(12), 0.5, 0.5)],
            [CreateVisualTree(start.AddSeconds(1), "card", "Card")]);
        var result = TimelineTaskExtractor.Extract(snapshot, start, start.AddSeconds(14), "Hold card");
        Assert.Equal(0, result.GeneratedActionCount);
        Assert.Contains(result.Diagnostics, value => value.Contains("unsupported hold duration"));
    }
}
