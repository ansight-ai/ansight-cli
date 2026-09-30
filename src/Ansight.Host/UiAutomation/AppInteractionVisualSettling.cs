using SkiaSharp;

namespace Ansight.Host.UiAutomation;

/// <summary>
/// Bounded visual quietness, not an assertion that an asynchronous operation finished.
/// A delayed transition must not be mistaken for stability of the pre-action screen.
/// </summary>
internal sealed class AppInteractionVisualSettling(AppInteractionVisualSample? baseline)
{
    internal const int MaximumMilliseconds = 2_000;
    internal const int QuietMilliseconds = 300;
    internal const int UnchangedGraceMilliseconds = 900;
    private AppInteractionVisualSample? anchor;
    private double quietSince;
    private bool changed;

    public int Samples { get; private set; }

    public void Observe(AppInteractionVisualSample sample, double elapsedMs)
    {
        Samples++;
        if (baseline is not null && sample.DiffersFrom(baseline)) changed = true;
        // Compare to the quiet-window anchor, not the immediately preceding sample:
        // small incremental animation changes must accumulate instead of disappearing.
        if (anchor is null || sample.DiffersFrom(anchor))
        {
            anchor = sample;
            quietSince = elapsedMs;
        }
    }

    public bool IsQuiet(double elapsedMs) => Samples >= 2
        && elapsedMs - quietSince >= QuietMilliseconds
        && (baseline is null || changed || elapsedMs >= UnchangedGraceMilliseconds);

    public AppInteractionSettling Describe(double elapsedMs) => new(
        IsQuiet(elapsedMs) ? baseline is not null && !changed ? "unchanged" : "stable" : "timed_out",
        Samples, elapsedMs);
}

internal sealed record AppInteractionVisualSample(int Width, int Height, SKColor[] Pixels)
{
    public static AppInteractionVisualSample FromPng(byte[] bytes)
    {
        using var source = SKBitmap.Decode(bytes)
            ?? throw new InvalidOperationException("The interaction screenshot could not be decoded.");
        const int width = 96;
        var height = Math.Clamp((int)Math.Round(source.Height * (width / (double)source.Width)), 1, 384);
        using var resized = source.Resize(new SKImageInfo(width, height), SKSamplingOptions.Default)
            ?? throw new InvalidOperationException("The interaction screenshot could not be sampled.");
        return new(width, height, resized.Pixels);
    }

    public bool DiffersFrom(AppInteractionVisualSample other)
    {
        if (Width != other.Width || Height != other.Height) return true;
        var different = 0;
        for (var index = 0; index < Pixels.Length; index++)
        {
            var left = Pixels[index];
            var right = other.Pixels[index];
            if (Math.Abs(left.Red - right.Red) > 12 || Math.Abs(left.Green - right.Green) > 12
                || Math.Abs(left.Blue - right.Blue) > 12 || Math.Abs(left.Alpha - right.Alpha) > 12)
                different++;
        }
        // Ignore isolated compression/rasterization noise and a blinking caret.
        return different > Pixels.Length * 0.002;
    }
}
