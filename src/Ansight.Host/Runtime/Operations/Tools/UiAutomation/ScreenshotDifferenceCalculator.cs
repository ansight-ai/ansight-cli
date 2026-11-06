using System.Text.Json.Nodes;
using Ansight.Tools;
using SkiaSharp;

namespace Ansight.Host.Runtime.Operations.Tools.UiAutomation;

internal static class ScreenshotDifferenceCalculator
{
    public static ScreenshotDifference Compare(
        string baselinePath,
        string currentPath,
        int maxWidth,
        int pixelThreshold,
        IReadOnlyList<ScreenshotIgnoreRegion>? ignoreRegions = null,
        CancellationToken token = default)
    {
        using var baselineSource = SKBitmap.Decode(baselinePath)
            ?? throw new InvalidOperationException("The baseline image could not be decoded.");
        using var currentSource = SKBitmap.Decode(currentPath)
            ?? throw new InvalidOperationException("The current image could not be decoded.");
        var targetWidth = Math.Min(maxWidth, baselineSource.Width);
        var targetHeight = Math.Max(1, (int)Math.Round(
            baselineSource.Height * (targetWidth / (double)baselineSource.Width)));
        var mask = ScreenshotIgnoreRegions.Rasterize(ignoreRegions ?? [], targetWidth, targetHeight, token);
        using var baseline = Resize(baselineSource, targetWidth, targetHeight);
        using var current = Resize(currentSource, targetWidth, targetHeight);
        var baselinePixels = baseline.Pixels;
        var currentPixels = current.Pixels;
        var differentPixels = 0L;
        var absoluteChannelDifference = 0L;

        for (var index = 0; index < baselinePixels.Length; index++)
        {
            if (index % targetWidth == 0)
            {
                token.ThrowIfCancellationRequested();
            }

            if (mask.Pixels.Length > 0 && mask.Pixels[index])
            {
                continue;
            }

            var left = baselinePixels[index];
            var right = currentPixels[index];
            var red = Math.Abs(left.Red - right.Red);
            var green = Math.Abs(left.Green - right.Green);
            var blue = Math.Abs(left.Blue - right.Blue);
            var alpha = Math.Abs(left.Alpha - right.Alpha);
            absoluteChannelDifference += red + green + blue + alpha;

            if (Math.Max(Math.Max(red, green), Math.Max(blue, alpha)) > pixelThreshold)
            {
                differentPixels++;
            }
        }

        var totalPixels = baselinePixels.LongLength - mask.IgnoredPixelCount;

        return new ScreenshotDifference(
            targetWidth,
            targetHeight,
            differentPixels,
            totalPixels,
            totalPixels == 0 ? 0 : differentPixels / (double)totalPixels,
            totalPixels == 0 ? 0 : absoluteChannelDifference / (double)(totalPixels * 4),
            mask.IgnoredPixelCount);
    }

    private static SKBitmap Resize(SKBitmap source, int width, int height)
    {
        if (source.Width == width && source.Height == height)
        {
            return source.Copy();
        }

        var resized = source.Resize(new SKImageInfo(width, height), SKSamplingOptions.Default);

        return resized ?? throw new InvalidOperationException("The screenshot could not be resized for comparison.");
    }
}
