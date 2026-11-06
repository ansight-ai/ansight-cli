namespace Ansight.Host.Apps;

public sealed record AppWatchDefinition(
    string Id, string AppId, string? Platform, string? DeviceId, bool Enabled, string[] CaptureFiles,
    bool CaptureInstruments = false, int ScreenshotIntervalMilliseconds = AppWatchDefinition.DefaultScreenshotIntervalMilliseconds)
{
    public const int DefaultScreenshotIntervalMilliseconds = 2_000;
    public const int MinimumScreenshotIntervalMilliseconds = 100;
    public const int MaximumScreenshotIntervalMilliseconds = 60_000;

    public static void ValidateScreenshotInterval(int intervalMilliseconds)
    {
        if (intervalMilliseconds is < MinimumScreenshotIntervalMilliseconds or > MaximumScreenshotIntervalMilliseconds)
        {
            throw new ArgumentOutOfRangeException(nameof(intervalMilliseconds),
                $"Screenshot interval must be between {MinimumScreenshotIntervalMilliseconds} and {MaximumScreenshotIntervalMilliseconds} milliseconds.");
        }
    }
}
