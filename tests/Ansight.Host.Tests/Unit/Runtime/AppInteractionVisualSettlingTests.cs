using Ansight.Host.UiAutomation;
using SkiaSharp;

namespace Ansight.Host.Tests.Unit.Runtime;

public sealed class AppInteractionVisualSettlingTests
{
    [Fact]
    public void OldScreenCannotSettleBeforeADelayedTransition()
    {
        var before = Solid(SKColors.Black);
        var after = Solid(SKColors.White);
        var settling = new AppInteractionVisualSettling(before);
        settling.Observe(before, 0);
        settling.Observe(before, 300);
        Assert.False(settling.IsQuiet(300));
        settling.Observe(after, 500);
        settling.Observe(after, 700);
        Assert.False(settling.IsQuiet(700));
        settling.Observe(after, 800);
        Assert.Equal("stable", settling.Describe(800).Status);
    }

    [Fact]
    public void NoChangeIsBoundedAndExplicitNotProofOfSemanticCompletion()
    {
        var screen = Solid(SKColors.White);
        var settling = new AppInteractionVisualSettling(screen);
        settling.Observe(screen, 0);
        settling.Observe(screen, 899);
        Assert.False(settling.IsQuiet(899));
        settling.Observe(screen, 900);
        Assert.Equal("unchanged", settling.Describe(900).Status);
    }

    [Fact]
    public void OngoingAnimationAndLateTransitionDoNotClaimStability()
    {
        var settling = new AppInteractionVisualSettling(null);
        settling.Observe(Solid(SKColors.Black), 0);
        settling.Observe(Solid(SKColors.White), 300);
        Assert.False(settling.IsQuiet(300));
        settling.Observe(Solid(SKColors.White), 600);
        Assert.True(settling.IsQuiet(600));
        settling.Observe(Solid(SKColors.Red), 610); // full capture verification saw navigation
        Assert.False(settling.IsQuiet(610));
        settling.Observe(Solid(SKColors.Black), 2_000);
        Assert.Equal("timed_out", settling.Describe(2_000).Status);
    }

    [Fact]
    public void SlowAnimationAccumulatesAgainstQuietWindowAnchor()
    {
        var settling = new AppInteractionVisualSettling(null);
        settling.Observe(Solid(new SKColor(0, 0, 0)), 0);
        settling.Observe(Solid(new SKColor(10, 10, 10)), 100);
        settling.Observe(Solid(new SKColor(20, 20, 20)), 200);
        Assert.False(settling.IsQuiet(300));
    }

    [Fact]
    public void IgnoresIsolatedCaretPixelsButDetectsTextAndOrientationChanges()
    {
        var baseline = Solid(SKColors.Black);
        var pixels = baseline.Pixels.ToArray();
        pixels[0] = SKColors.White;
        Assert.False(new AppInteractionVisualSample(100, 100, pixels).DiffersFrom(baseline));
        Array.Fill(pixels, SKColors.White, 0, 100);
        Assert.True(new AppInteractionVisualSample(100, 100, pixels).DiffersFrom(baseline));
        Assert.True(new AppInteractionVisualSample(50, 200, baseline.Pixels).DiffersFrom(baseline));
    }

    private static AppInteractionVisualSample Solid(SKColor color)
        => new(100, 100, Enumerable.Repeat(color, 10_000).ToArray());
}
