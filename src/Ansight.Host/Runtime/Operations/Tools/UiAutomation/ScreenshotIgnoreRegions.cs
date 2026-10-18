using System.Text.Json.Nodes;
using Ansight.Tools;

namespace Ansight.Host.Runtime.Operations.Tools.UiAutomation;

internal sealed record ScreenshotIgnoreRegion(double X, double Y, double Width, double Height);
internal sealed record ScreenshotIgnoreMask(bool[] Pixels, long IgnoredPixelCount);

internal static class ScreenshotIgnoreRegions
{
    internal const int MaximumRegions = 64;

    public static ToolSchema Schema() => ToolSchema.Array(ToolSchema.Object(
        properties: new Dictionary<string, ToolSchema>
        {
            ["x"] = ToolSchema.Number("Left edge, normalized from 0 to 1 in the full actual image."),
            ["y"] = ToolSchema.Number("Top edge, normalized from 0 to 1 in the full actual image."),
            ["width"] = ToolSchema.Number("Positive normalized width; x + width must be at most 1."),
            ["height"] = ToolSchema.Number("Positive normalized height; y + height must be at most 1.")
        }, required: ["x", "y", "width", "height"], additionalProperties: false),
        description: "Up to 64 rectangles excluded from pixel scores and changed regions. Overlaps count once. Must leave pixels to compare.", nullable: true);

    public static IReadOnlyList<ScreenshotIgnoreRegion> Read(JsonNode? node)
    {
        if (node is null) return [];
        if (node is not JsonArray array || array.Count > MaximumRegions)
            throw new ArgumentException($"ignoreRegions must be an array of at most {MaximumRegions} rectangles.");
        var regions = new List<ScreenshotIgnoreRegion>();
        for (var i = 0; i < array.Count; i++)
        {
            if (array[i] is not JsonObject item || item.Count != 4)
                throw new ArgumentException($"ignoreRegions[{i}] must contain exactly x, y, width and height.");
            var x = Coordinate(item["x"], i, "x");
            var y = Coordinate(item["y"], i, "y");
            var width = Coordinate(item["width"], i, "width");
            var height = Coordinate(item["height"], i, "height");
            if (x < 0 || y < 0 || width <= 0 || height <= 0 || x >= 1 || y >= 1
                || width > 1 || height > 1 || x + width > 1 || y + height > 1)
                throw new ArgumentException($"ignoreRegions[{i}] must have positive dimensions and fit within normalized image bounds (0–1).");
            regions.Add(new ScreenshotIgnoreRegion(x, y, width, height));
        }
        return regions;
    }

    public static JsonArray Describe(IReadOnlyList<ScreenshotIgnoreRegion> regions) => new(regions.Select(region =>
        (JsonNode)new JsonObject { ["x"] = region.X, ["y"] = region.Y, ["width"] = region.Width, ["height"] = region.Height }).ToArray());

    public static ScreenshotIgnoreMask Rasterize(IReadOnlyList<ScreenshotIgnoreRegion> regions,
        int width, int height, CancellationToken token)
    {
        if (regions.Count == 0) return new ScreenshotIgnoreMask([], 0);
        var pixels = new bool[checked(width * height)];
        var count = 0L;
        foreach (var region in regions)
        {
            // Round outward so any comparison pixel touched by a rectangle is excluded.
            var left = Math.Clamp((int)Math.Floor(Snap(region.X * width)), 0, width - 1);
            var top = Math.Clamp((int)Math.Floor(Snap(region.Y * height)), 0, height - 1);
            var right = Math.Clamp((int)Math.Ceiling(Snap((region.X + region.Width) * width)), left + 1, width);
            var bottom = Math.Clamp((int)Math.Ceiling(Snap((region.Y + region.Height) * height)), top + 1, height);
            for (var y = top; y < bottom; y++)
            {
                token.ThrowIfCancellationRequested();
                for (var x = left; x < right; x++)
                {
                    var index = y * width + x;
                    if (pixels[index]) continue;
                    pixels[index] = true;
                    count++;
                }
            }
        }
        if (count == pixels.LongLength)
            throw new ArgumentException("ignoreRegions cover every pixel at the comparison resolution. Leave at least one pixel unmasked.");
        return new ScreenshotIgnoreMask(pixels, count);
    }

    private static double Snap(double value)
    {
        var integer = Math.Round(value);
        return Math.Abs(value - integer) < 1e-9 ? integer : value;
    }

    private static double Coordinate(JsonNode? node, int index, string name)
    {
        if (node is JsonValue value)
        {
            if (value.TryGetValue<double>(out var number) && double.IsFinite(number)) return number;
            if (value.TryGetValue<int>(out var integer)) return integer;
            if (value.TryGetValue<long>(out var longInteger)) return longInteger;
        }
        throw new ArgumentException($"ignoreRegions[{index}].{name} must be a finite number.");
    }
}
