namespace Ansight.Host.Runtime.Screenshots;

using SkiaSharp;

internal static class ExternalSessionScreenshotFrameEncoder
{
    public static EncodedExternalSessionScreenshot Encode(
        byte[] capturedBytes,
        ExternalSessionScreenshotCaptureProfile profile)
    {
        ArgumentNullException.ThrowIfNull(capturedBytes);
        ArgumentNullException.ThrowIfNull(profile);

        using var source = SKBitmap.Decode(capturedBytes)
            ?? throw new InvalidOperationException("The external screenshot could not be decoded.");
        var targetWidth = profile.ResolveTargetWidth(source.Width);
        var targetHeight = Math.Max(1, (int)Math.Round(source.Height * (targetWidth / (double)source.Width)));
        using var target = targetWidth == source.Width
            ? source.Copy()
            : source.Resize(
                new SKImageInfo(targetWidth, targetHeight),
                new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
        if (target is null)
        {
            throw new InvalidOperationException("The external screenshot could not be resized.");
        }

        using var image = SKImage.FromBitmap(target);
        using var encoded = image.Encode(SKEncodedImageFormat.Webp, profile.Quality);
        return new EncodedExternalSessionScreenshot(
            "webp",
            target.Width,
            target.Height,
            profile.Quality,
            encoded.ToArray());
    }
}

internal sealed record EncodedExternalSessionScreenshot(
    string Format,
    int Width,
    int Height,
    int Quality,
    byte[] Bytes);
