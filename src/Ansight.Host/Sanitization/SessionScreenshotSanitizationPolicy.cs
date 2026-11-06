namespace Ansight.Host.Sanitization;

internal sealed class SessionScreenshotSanitizationPolicy
{
    public SessionScreenshotSanitizationMode Mode { get; init; } = SessionScreenshotSanitizationMode.SensitiveRegions;

    public SessionScreenshotSanitizationFallback Fallback { get; init; } = SessionScreenshotSanitizationFallback.RedactAll;

    public int PaddingPixels { get; init; } = 6;
}
