using System.Text.Json.Nodes;

namespace Ansight.Host.Models.Session;

internal static class ScreenshotGeometry
{
    internal static readonly string[] Kinds = ["statusBar", "navigationBar", "displayCutout"];

    // Treat malformed metadata as unavailable; it must never produce a guessed comparison mask.
    internal static JsonObject? Validate(JsonNode? value)
    {
        try
        {
            if (value is not JsonObject geometry || geometry["coordinateSpace"]?.GetValue<string>() != "image-normalized"
                || geometry["captureTarget"]?.GetValue<string>() is not ("display" or "appWindow" or "appContent")
                || geometry["systemUi"] is not JsonObject systemUi) return null;
            if (geometry["safeAreaInsets"] is { } safe)
            {
                if (safe is not JsonObject || !Unit(safe["top"], out var top) || !Unit(safe["right"], out var right)
                    || !Unit(safe["bottom"], out var bottom) || !Unit(safe["left"], out var left)
                    || top + bottom >= 1 || left + right >= 1) return null;
            }
            var count = 0;
            foreach (var kind in Kinds)
            {
                if (systemUi[kind] is not JsonObject entry || entry["regions"] is not JsonArray regions
                    || entry["state"]?.GetValue<string>() is not ("present" or "absent" or "unknown")) return null;
                var state = entry["state"]!.GetValue<string>();
                if ((state == "present") != (regions.Count > 0)) return null;
                if (state == "unknown" && string.IsNullOrWhiteSpace(entry["reason"]?.GetValue<string>())) return null;
                count += regions.Count;
                if (count > 64) return null;
                foreach (var rectangle in regions)
                {
                    if (rectangle is not JsonObject || !Unit(rectangle["x"], out var x) || !Unit(rectangle["y"], out var y)
                        || !Unit(rectangle["width"], out var width) || !Unit(rectangle["height"], out var height)
                        || width <= 0 || height <= 0 || x + width > 1 || y + height > 1) return null;
                }
            }
            return geometry.DeepClone().AsObject();
        }
        catch (Exception error) when (error is InvalidOperationException or FormatException or JsonException) { return null; }
    }

    private static bool Unit(JsonNode? node, out double value)
    {
        value = 0;
        return node is JsonValue scalar && scalar.TryGetValue(out value) && double.IsFinite(value) && value >= 0 && value <= 1;
    }
}
