using System.Text.Json.Nodes;
using SkiaSharp;

namespace Ansight.Host.Runtime.Operations.Tools.UiAutomation;

internal static class ScreenshotComparison
{
    internal const int MaximumRegions = 64;

    public static JsonObject Compare(InspectionImage actual, InspectionImage reference, int maxWidth,
        int pixelThreshold, double maxDifferenceRatio, bool resizeToReference, IReadOnlyList<ScreenshotIgnoreRegion> ignoreRegions,
        ScreenshotInspectionEvidence evidence, CancellationToken token)
    {
        var result = new JsonObject
        {
            ["capability"] = "screenshot.compare", ["image"] = actual.Describe(), ["reference"] = reference.Describe(),
            ["pixelThreshold"] = pixelThreshold, ["maxDifferenceRatio"] = maxDifferenceRatio,
            ["resizeToReference"] = resizeToReference, ["regions"] = new JsonArray(),
            ["ignoreRegions"] = ScreenshotIgnoreRegions.Describe(ignoreRegions)
        };
        result["image"]!["artifact"] = evidence.WriteImage("actual", actual);
        result["reference"]!["artifact"] = evidence.WriteImage("reference", reference);
        var dimensionsMatch = actual.Width == reference.Width && actual.Height == reference.Height;
        result["dimensionsMatch"] = dimensionsMatch;
        if (!dimensionsMatch && !resizeToReference)
        {
            result["passed"] = false;
            result["status"] = "dimension-mismatch";
            result["message"] = $"Actual image is {actual.Width}×{actual.Height}; reference is {reference.Width}×{reference.Height}. Set resizeToReference explicitly to compare at the reference proportions.";
            return result;
        }
        var width = Math.Min(maxWidth, reference.Width);
        var height = Math.Max(1, (int)Math.Round(reference.Height * (width / (double)reference.Width)));
        var mask = ScreenshotIgnoreRegions.Rasterize(ignoreRegions, width, height, token);
        using var leftSource = SKBitmap.Decode(reference.Bytes) ?? throw new InvalidDataException("Reference image could not be decoded.");
        using var rightSource = SKBitmap.Decode(actual.Bytes) ?? throw new InvalidDataException("Actual image could not be decoded.");
        using var left = Resize(leftSource, width, height);
        using var right = Resize(rightSource, width, height);
        var baselinePixels = left.Pixels;
        var actualPixels = right.Pixels;
        var changed = new bool[actualPixels.Length];
        var count = 0L;
        var channelDifference = 0L;
        for (var i = 0; i < changed.Length; i++)
        {
            if (i % width == 0) token.ThrowIfCancellationRequested();
            if (mask.Pixels.Length > 0 && mask.Pixels[i]) continue;
            var a = actualPixels[i];
            var b = baselinePixels[i];
            var red = Math.Abs(a.Red - b.Red);
            var green = Math.Abs(a.Green - b.Green);
            var blue = Math.Abs(a.Blue - b.Blue);
            var alpha = Math.Abs(a.Alpha - b.Alpha);
            channelDifference += red + green + blue + alpha;
            changed[i] = Math.Max(Math.Max(red, green), Math.Max(blue, alpha)) > pixelThreshold;
            if (changed[i])
            {
                count++;
                // Paint changed pixels over the actual image, retaining local context.
                actualPixels[i] = new SKColor((byte)((a.Red + 255) / 2), (byte)(a.Green / 2), (byte)((a.Blue + 80) / 2), 255);
            }
        }
        right.Pixels = actualPixels;
        using var overlay = right.Encode(SKEncodedImageFormat.Png, 100);
        result["overlay"] = evidence.Write("overlay.png", overlay.ToArray());
        var regionResult = FindRegions(changed, width, height, actual.FrameId!, token);
        result["regions"] = regionResult.Regions;
        result["regionCount"] = regionResult.Count;
        result["regionsTruncated"] = regionResult.Count > MaximumRegions;
        var totalPixels = changed.LongLength - mask.IgnoredPixelCount;
        var ratio = count / (double)totalPixels;
        var passed = ratio <= maxDifferenceRatio;
        result["passed"] = passed;
        result["status"] = "compared";
        result["width"] = width;
        result["height"] = height;
        result["differentPixelCount"] = count;
        result["totalPixelCount"] = totalPixels;
        result["ignoredPixelCount"] = mask.IgnoredPixelCount;
        result["differenceRatio"] = ratio;
        result["meanChannelDifference"] = channelDifference / (double)(totalPixels * 4L);
        result["message"] = passed ? "Screenshot comparison passed."
            : $"Screenshot difference ratio {ratio:0.######} exceeded {maxDifferenceRatio:0.######}.";
        return result;
    }

    private static SKBitmap Resize(SKBitmap bitmap, int width, int height) =>
        bitmap.Width == width && bitmap.Height == height ? bitmap.Copy()
            : bitmap.Resize(new SKImageInfo(width, height), SKSamplingOptions.Default)
                ?? throw new InvalidDataException("Image could not be resized.");

    private sealed record Component(int Left, int Top, int Right, int Bottom, int PixelCount);
    private sealed record RegionResult(JsonArray Regions, int Count);

    private static RegionResult FindRegions(bool[] mask, int width, int height, string frameId, CancellationToken token)
    {
        var queue = new Queue<int>();
        var largest = new PriorityQueue<Component, int>();
        var componentCount = 0;
        for (var i = 0; i < mask.Length; i++)
        {
            if (i % width == 0) token.ThrowIfCancellationRequested();
            if (!mask[i]) continue;
            mask[i] = false;
            queue.Enqueue(i);
            var left = i % width;
            var right = left;
            var top = i / width;
            var bottom = top;
            var pixelCount = 0;
            while (queue.TryDequeue(out var index))
            {
                if (++pixelCount % 4096 == 0) token.ThrowIfCancellationRequested();
                var x = index % width;
                var y = index / width;
                left = Math.Min(left, x); right = Math.Max(right, x);
                top = Math.Min(top, y); bottom = Math.Max(bottom, y);
                for (var dy = -1; dy <= 1; dy++)
                for (var dx = -1; dx <= 1; dx++)
                {
                    var nextX = x + dx;
                    var nextY = y + dy;
                    if (nextX < 0 || nextX >= width || nextY < 0 || nextY >= height) continue;
                    var next = nextY * width + nextX;
                    if (!mask[next]) continue;
                    mask[next] = false;
                    queue.Enqueue(next);
                }
            }
            componentCount++;
            largest.Enqueue(new Component(left, top, right, bottom, pixelCount), pixelCount);
            if (largest.Count > MaximumRegions) largest.Dequeue();
        }
        var regions = new JsonArray();
        foreach (var component in largest.UnorderedItems.Select(item => item.Element)
            .OrderByDescending(item => item.PixelCount).ThenBy(item => item.Top).ThenBy(item => item.Left))
        {
            var id = (regions.Count + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
            var region = ScreenshotInspectionEvidence.Region(id, $"Changed area {id}", "pixel-difference", frameId,
                component.Left / (double)width, component.Top / (double)height,
                (component.Right - component.Left + 1) / (double)width, (component.Bottom - component.Top + 1) / (double)height);
            region["differentPixelCount"] = component.PixelCount;
            regions.Add(region);
        }
        return new RegionResult(regions, componentCount);
    }
}
