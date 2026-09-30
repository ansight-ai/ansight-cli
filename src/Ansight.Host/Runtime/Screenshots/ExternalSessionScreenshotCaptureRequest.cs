namespace Ansight.Host.Runtime.Screenshots;

using System.Text.Json.Nodes;

internal sealed record ExternalSessionScreenshotCaptureRequest(int? MaxWidth, int IntervalMilliseconds = 2_000)
{
    private const int defaultMaxWidth = 480;
    private const int maximumAllowedWidth = 8_192;

    public static ExternalSessionScreenshotCaptureRequest Default { get; } = new(defaultMaxWidth);

    public static ExternalSessionScreenshotCaptureRequest FromPayload(JsonObject? profilePayload)
    {
        if (profilePayload?["sessionJpegCapture"] is not JsonObject capture
            || !capture.ContainsKey("maxWidth"))
        {
            return Default;
        }

        var maxWidthNode = capture["maxWidth"];
        if (maxWidthNode is null)
        {
            return new ExternalSessionScreenshotCaptureRequest((int?)null);
        }

        return maxWidthNode is JsonValue maxWidthValue
               && maxWidthValue.TryGetValue<int>(out var maxWidth)
               && maxWidth > 0
            ? new ExternalSessionScreenshotCaptureRequest(Math.Min(maxWidth, maximumAllowedWidth))
            : Default;
    }

    public int ResolveTargetWidth(int sourceWidth)
        => MaxWidth.HasValue
            ? Math.Min(MaxWidth.Value, sourceWidth)
            : sourceWidth;
}
