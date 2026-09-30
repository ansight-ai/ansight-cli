using System.Globalization;
using System.Text.Json.Nodes;
using Ansight.Host;

namespace Ansight.Host.Runtime.Operations.Tools.UiAutomation;

/// <summary>
/// Builds a consumer-facing view of a raw visual tree without changing the captured evidence.
/// </summary>
internal static class VisualTreePresentationNormalizer
{
    private const double CoverageTolerance = 0.5;
    private const double OpaqueThreshold = 0.999;

    public static JsonObject Create(JsonObject rawPayload)
    {
        ArgumentNullException.ThrowIfNull(rawPayload);
        var presentation = rawPayload.DeepClone() as JsonObject ?? new JsonObject();
        if (!ShouldNormalize(presentation)
            || !VisualTreeTypeRegistry.TryCreateCompactV2(
                presentation,
                out var typeRegistry,
                out var root)
            || typeRegistry is null
            || root is null)
        {
            return presentation;
        }

        NormalizeBranch(root, ancestorsVisible: true, typeRegistry);
        return presentation;
    }

    public static void NormalizeSerializedSession(JsonNode? serializedSession)
    {
        if (serializedSession is not JsonObject session
            || session["visualTreeSnapshots"] is not JsonArray snapshots)
        {
            return;
        }

        foreach (var snapshot in snapshots.OfType<JsonObject>())
        {
            if (snapshot["payload"] is JsonObject rawPayload)
            {
                snapshot["payload"] = Create(rawPayload);
            }
        }
    }

    private static void NormalizeBranch(
        JsonObject node,
        bool ancestorsVisible,
        VisualTreeTypeRegistry typeRegistry)
    {
        var locallyVisible = LiveUiNodeQuery.ReadBoolean(
            node,
            "visible",
            fallback: true,
            typeRegistry);
        var effectivelyVisible = ancestorsVisible && locallyVisible;
        node["visible"] = effectivelyVisible;

        if (node["children"] is not JsonArray children)
        {
            return;
        }

        var childNodes = children.OfType<JsonObject>().ToArray();
        for (var index = 0; index < childNodes.Length; index++)
        {
            var child = childNodes[index];
            var childVisible = effectivelyVisible
                               && LiveUiNodeQuery.ReadBoolean(
                                   child,
                                   "visible",
                                   fallback: true,
                                   typeRegistry)
                               && !IsCoveredByFrontSibling(
                                   child,
                                   index,
                                   childNodes,
                                   typeRegistry);
            NormalizeBranch(child, childVisible, typeRegistry);
        }
    }

    private static bool IsCoveredByFrontSibling(
        JsonObject target,
        int targetIndex,
        IReadOnlyList<JsonObject> siblings,
        VisualTreeTypeRegistry typeRegistry)
    {
        if (LiveUiNodeQuery.ReadBounds(target) is not { Width: > 0, Height: > 0 } targetBounds)
        {
            return false;
        }

        var targetZ = ReadNumber(target, "z") ?? 0;
        for (var candidateIndex = 0; candidateIndex < siblings.Count; candidateIndex++)
        {
            if (candidateIndex == targetIndex)
            {
                continue;
            }

            var candidate = siblings[candidateIndex];
            var candidateZ = ReadNumber(candidate, "z") ?? 0;
            var isInFront = candidateZ > targetZ
                            || NearlyEqual(candidateZ, targetZ) && candidateIndex > targetIndex;
            if (!isInFront
                || !LiveUiNodeQuery.ReadBoolean(
                    candidate,
                    "visible",
                    fallback: true,
                    typeRegistry))
            {
                continue;
            }

            if (ContainsOpaqueSurfaceCovering(
                    candidate,
                    targetBounds,
                    cumulativeOpacity: 1,
                    typeRegistry))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsOpaqueSurfaceCovering(
        JsonObject node,
        LiveUiBounds targetBounds,
        double cumulativeOpacity,
        VisualTreeTypeRegistry typeRegistry)
    {
        if (!LiveUiNodeQuery.ReadBoolean(node, "visible", fallback: true, typeRegistry))
        {
            return false;
        }

        var effectiveOpacity = cumulativeOpacity * ReadOpacity(node);
        if (effectiveOpacity < OpaqueThreshold)
        {
            return false;
        }

        if (LiveUiNodeQuery.ReadBounds(node) is { Width: > 0, Height: > 0 } bounds
            && Covers(bounds, targetBounds)
            && (HasOpaqueBackground(node) || IsSemanticPageSurface(node, typeRegistry)))
        {
            return true;
        }

        return node["children"] is JsonArray children
               && children.OfType<JsonObject>().Any(child =>
                   ContainsOpaqueSurfaceCovering(
                       child,
                       targetBounds,
                       effectiveOpacity,
                       typeRegistry));
    }

    private static bool IsSemanticPageSurface(
        JsonObject node,
        VisualTreeTypeRegistry typeRegistry)
    {
        if (LiveUiNodeQuery.ReadSupportedActions(node, typeRegistry)
            .Contains("tap", StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        var automationId = LiveUiNodeQuery.ReadAutomationId(node);
        var type = typeRegistry.Resolve(node);
        return automationId?.EndsWith("Page", StringComparison.OrdinalIgnoreCase) == true
               || type?.EndsWith("Page", StringComparison.OrdinalIgnoreCase) == true
               || type?.EndsWith("ViewController", StringComparison.OrdinalIgnoreCase) == true;
    }

    private static bool HasOpaqueBackground(JsonObject node)
    {
        var background = ReadNestedString(node, "visual", "background")
                         ?? ReadNestedString(node, "visual", "backgroundColor")
                         ?? ReadNestedString(node, "properties", "backgroundColor");
        if (background is null)
        {
            return false;
        }

        if (string.Equals(background, "transparent", StringComparison.OrdinalIgnoreCase)
            || string.Equals(background, "clear", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (TryReadHexAlpha(background, out var alpha))
        {
            return alpha >= OpaqueThreshold;
        }

        if (background.StartsWith("rgba(", StringComparison.OrdinalIgnoreCase)
            && background.EndsWith(')'))
        {
            var components = background[5..^1].Split(',');
            return components.Length == 4
                   && double.TryParse(
                       components[3],
                       NumberStyles.Float,
                       CultureInfo.InvariantCulture,
                       out var rgbaAlpha)
                   && rgbaAlpha >= OpaqueThreshold;
        }

        return true;
    }

    private static bool TryReadHexAlpha(string color, out double alpha)
    {
        alpha = 0;
        if (!color.StartsWith('#'))
        {
            return false;
        }

        if (color.Length is 4 or 7)
        {
            alpha = 1;
            return true;
        }

        if (color.Length == 5
            && int.TryParse(
                color.AsSpan(1, 1),
                NumberStyles.HexNumber,
                CultureInfo.InvariantCulture,
                out var shortAlpha))
        {
            alpha = shortAlpha / 15d;
            return true;
        }

        if (color.Length == 9
            && int.TryParse(
                color.AsSpan(1, 2),
                NumberStyles.HexNumber,
                CultureInfo.InvariantCulture,
                out var fullAlpha))
        {
            alpha = fullAlpha / 255d;
            return true;
        }

        return false;
    }

    private static double ReadOpacity(JsonObject node)
        => ReadNestedNumber(node, "visual", "opacity")
           ?? ReadNumber(node, "opacity")
           ?? 1;

    private static string? ReadNestedString(
        JsonObject node,
        string objectName,
        string propertyName)
        => node[objectName] is JsonObject nested
            ? LiveUiNodeQuery.ReadString(nested, propertyName)
            : null;

    private static double? ReadNestedNumber(
        JsonObject node,
        string objectName,
        string propertyName)
        => node[objectName] is JsonObject nested
            ? ReadNumber(nested, propertyName)
            : null;

    private static double? ReadNumber(JsonObject node, string propertyName)
    {
        if (node[propertyName] is not JsonValue value)
        {
            return null;
        }

        if (value.TryGetValue<double>(out var doubleValue))
        {
            return doubleValue;
        }

        if (value.TryGetValue<int>(out var integerValue))
        {
            return integerValue;
        }

        return null;
    }

    private static bool Covers(LiveUiBounds candidate, LiveUiBounds target)
        => candidate.X <= target.X + CoverageTolerance
           && candidate.Y <= target.Y + CoverageTolerance
           && candidate.X + candidate.Width >= target.X + target.Width - CoverageTolerance
           && candidate.Y + candidate.Height >= target.Y + target.Height - CoverageTolerance;

    private static bool NearlyEqual(double left, double right)
        => Math.Abs(left - right) <= double.Epsilon * Math.Max(1, Math.Max(Math.Abs(left), Math.Abs(right))) * 8;

    private static bool ShouldNormalize(JsonObject payload)
    {
        if (!string.Equals(
                LiveUiNodeQuery.ReadString(payload, "format"),
                VisualTreeContract.NativeFormat,
                StringComparison.Ordinal))
        {
            return false;
        }

        var adapter = LiveUiNodeQuery.ReadString(payload, "adapter");
        var platform = LiveUiNodeQuery.ReadString(payload, "platform");
        return string.Equals(adapter, "apple.uikit", StringComparison.OrdinalIgnoreCase)
               || string.Equals(platform, "ios", StringComparison.OrdinalIgnoreCase)
               || string.Equals(platform, "maccatalyst", StringComparison.OrdinalIgnoreCase);
    }
}
