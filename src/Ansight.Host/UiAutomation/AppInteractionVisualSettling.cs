using SkiaSharp;

namespace Ansight.Host.UiAutomation;

/// <summary>
/// Bounded visual quietness, not an assertion that an asynchronous operation finished.
/// A delayed transition must not be mistaken for stability of the pre-action screen.
/// </summary>
internal sealed class AppInteractionVisualSettling
{
    private readonly AppInteractionVisualSample? baseline;
    internal const int MaximumMilliseconds = 2_000;
    internal const int QuietMilliseconds = 300;
    internal const int UnchangedGraceMilliseconds = 900;
    private AppInteractionVisualSample? anchor;
    private double quietSince;
    private bool changed;

    public AppInteractionVisualSettling(AppInteractionVisualSample? baseline)
    {
        this.baseline = baseline;
    }

    public int Samples { get; private set; }

    public void Observe(AppInteractionVisualSample sample, double elapsedMs)
    {
        Samples++;

        if (baseline is not null && sample.DiffersFrom(baseline))
        {
            changed = true;
        }

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
