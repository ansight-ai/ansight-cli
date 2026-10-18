using System.Text.Json.Nodes;
using Ansight.Tools;

namespace Ansight.Host.Runtime.Operations.Tools.UiAutomation;

internal sealed record ScreenshotSystemUiMaskResolution(IReadOnlyList<ScreenshotIgnoreRegion> Regions, JsonArray Resolutions);

internal static class ScreenshotSystemUiMasks
{
    internal static ToolSchema Schema() => ToolSchema.Array(ToolSchema.String("System UI to exclude using the actual frame's capture metadata.",
        enumValues: ScreenshotGeometry.Kinds), nullable: true);

    internal static IReadOnlyList<string> Read(JsonNode? node)
    {
        if (node is null) return [];
        if (node is not JsonArray array || array.Count > 3) throw new ArgumentException("ignoreSystemUi must be an array of statusBar, navigationBar or displayCutout.");
        var kinds = new List<string>();
        foreach (var item in array)
        {
            if (item is not JsonValue value || !value.TryGetValue<string>(out var kind) || !ScreenshotGeometry.Kinds.Contains(kind))
                throw new ArgumentException("ignoreSystemUi accepts statusBar, navigationBar and displayCutout only.");
            if (!kinds.Contains(kind)) kinds.Add(kind);
        }
        return kinds;
    }

    internal static ScreenshotSystemUiMaskResolution Resolve(JsonNode? geometry, IReadOnlyList<string> kinds,
        IReadOnlyList<ScreenshotIgnoreRegion> manual, string? frameId)
    {
        var valid = ScreenshotGeometry.Validate(geometry);
        var regions = manual.ToList();
        var resolutions = new JsonArray();
        foreach (var kind in kinds)
        {
            var entry = valid?["systemUi"]?[kind];
            var state = entry?["state"]?.GetValue<string>();
            if (state is null or "unknown")
                throw new ScreenshotGeometryUnavailableException($"Cannot ignore {kind}: screenshot '{frameId}' has no reliable capture-time region. Capture with includeSystemUiMetadata: true and an updated SDK/provider, or supply manual ignoreRegions. {entry?["reason"]}");
            var resolved = ScreenshotIgnoreRegions.Read(entry!["regions"]);
            regions.AddRange(resolved);
            resolutions.Add(new JsonObject { ["kind"] = kind, ["state"] = state, ["frameId"] = frameId,
                ["regions"] = ScreenshotIgnoreRegions.Describe(resolved), ["reason"] = entry["reason"]?.DeepClone() });
        }
        if (regions.Count > 64) throw new ArgumentException("Combined manual and system-UI ignore regions must not exceed 64 rectangles.");
        return new(regions, resolutions);
    }
}

internal sealed class ScreenshotGeometryUnavailableException(string message) : ArgumentException(message);
