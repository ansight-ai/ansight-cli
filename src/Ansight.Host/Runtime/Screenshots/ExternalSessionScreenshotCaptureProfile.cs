namespace Ansight.Host.Runtime.Screenshots;

internal sealed record ExternalSessionScreenshotCaptureProfile(
    TimeSpan Interval,
    int Quality,
    int? MaxWidth)
{
    public const int TestRunIntervalMilliseconds = 400;
    public const int WebpQuality = 80;
    public const int TestRunMinimumWidth = 1_280;

    public static ExternalSessionScreenshotCaptureProfile Standard(
        ExternalSessionScreenshotCaptureRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new ExternalSessionScreenshotCaptureProfile(
            TimeSpan.FromMilliseconds(request.IntervalMilliseconds),
            WebpQuality,
            request.MaxWidth);
    }

    public static ExternalSessionScreenshotCaptureProfile TestRun(
        ExternalSessionScreenshotCaptureRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new ExternalSessionScreenshotCaptureProfile(
            TimeSpan.FromMilliseconds(TestRunIntervalMilliseconds),
            WebpQuality,
            request.MaxWidth.HasValue
                ? Math.Max(request.MaxWidth.Value, TestRunMinimumWidth)
                : null);
    }

    public int ResolveTargetWidth(int sourceWidth)
        => MaxWidth.HasValue
            ? Math.Min(MaxWidth.Value, sourceWidth)
            : sourceWidth;
}
