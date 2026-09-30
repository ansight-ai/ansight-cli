using System.Globalization;
using System.Text.Json.Nodes;
using Ansight.Host.Runtime.Operations.Tools.UiAutomation;

namespace Ansight.Host.Runtime.Operations.Tools.TouchReview;

internal static class TapTargetMatcher
{
    public static IEnumerable<JsonObject> FindTapTargetCandidates(
        AppSessionSnapshot snapshot,
        TouchGestureSegment gesture,
        NormalizedPoint point,
        TimeSpan window)
    {
        foreach (var annotation in snapshot.Annotations
                     .Where(annotation => TouchReviewGeometry.AnnotationOverlaps(annotation, gesture.StartUtc.Subtract(window), gesture.EndUtc.Add(window))))
        {
            foreach (var target in BuildAnnotationTapTargetCandidates(annotation, point))
            {
                yield return target;
            }
        }

        foreach (var visualTree in snapshot.VisualTreeSnapshots
                     .Where(visualTree => Math.Abs((visualTree.CapturedAtUtc - gesture.StartUtc).TotalMilliseconds) <= window.TotalMilliseconds)
                     .OrderBy(visualTree => Math.Abs((visualTree.CapturedAtUtc - gesture.StartUtc).TotalMilliseconds))
                     .Take(3))
        {
            var surface = ResolveVisualTreeSurface(snapshot, visualTree);
            var typeRegistry = VisualTreeTypeRegistry.FromPayload(visualTree.Payload);
            foreach (var target in BuildVisualTreeTapTargetCandidates(
                         visualTree,
                         visualTree.Payload["root"] as JsonObject,
                         point,
                         surface,
                         typeRegistry,
                         depth: 0))
            {
                yield return target;
            }
        }
    }

    public static JsonObject BuildTapTargetMatchPayload(
        TouchGestureSegment gesture,
        NormalizedPoint? point,
        IReadOnlyList<JsonObject> targetCandidates)
    {
        return new JsonObject
        {
            ["gesture"] = TouchReviewPayloads.BuildGesturePayload(gesture, includeTouches: false),
            ["point"] = TouchReviewPayloads.BuildPointPayload(point),
            ["targetCount"] = targetCandidates.Count,
            ["targetCandidates"] = PayloadJson.CreateJsonArray(targetCandidates.Select(target => (JsonNode?)target))
        };
    }

    public static IEnumerable<JsonObject> FindVisualTreeTapTargetCandidates(
        AppSessionSnapshot snapshot,
        SessionVisualTreeSnapshot visualTree,
        NormalizedPoint point)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(visualTree);
        var surface = ResolveVisualTreeSurface(snapshot, visualTree);
        var typeRegistry = VisualTreeTypeRegistry.FromPayload(visualTree.Payload);
        return BuildVisualTreeTapTargetCandidates(
            visualTree,
            visualTree.Payload["root"] as JsonObject,
            point,
            surface,
            typeRegistry,
            depth: 0);
    }

    private static IEnumerable<JsonObject> BuildAnnotationTapTargetCandidates(SessionAnnotation annotation, NormalizedPoint point)
    {
        if (annotation.Target?.NormalizedBounds is { } normalizedBounds && TouchReviewGeometry.BoundsContains(normalizedBounds, point))
        {
            yield return new JsonObject
            {
                ["source"] = "annotationTarget",
                ["annotationId"] = annotation.AnnotationId,
                ["label"] = annotation.Label,
                ["target"] = PayloadJson.BuildSessionAnnotationTargetPayload(annotation.Target),
                ["bounds"] = PayloadJson.BuildSessionAnnotationTargetBoundsPayload(normalizedBounds)
            };
        }

        foreach (var geometry in annotation.Geometry.Where(geometry => TouchReviewGeometry.GeometryContains(geometry, point)))
        {
            yield return new JsonObject
            {
                ["source"] = "annotationGeometry",
                ["annotationId"] = annotation.AnnotationId,
                ["label"] = annotation.Label,
                ["geometry"] = PayloadJson.BuildSessionAnnotationGeometryPayload(geometry)
            };
        }
    }

    private static IEnumerable<JsonObject> BuildVisualTreeTapTargetCandidates(
        SessionVisualTreeSnapshot visualTree,
        JsonObject? node,
        NormalizedPoint point,
        TouchSurface surface,
        VisualTreeTypeRegistry typeRegistry,
        int depth)
    {
        if (node is null)
        {
            yield break;
        }

        if (TryReadNodeBounds(node, surface, out var bounds) && TouchReviewGeometry.BoundsContains(bounds, point))
        {
            yield return new JsonObject
            {
                ["source"] = "visualTree",
                ["visualTreeSnapshotId"] = visualTree.SnapshotId,
                ["capturedAtUtc"] = visualTree.CapturedAtUtc,
                ["nodeId"] = ReadNodeString(node, "id"),
                ["type"] = typeRegistry.Resolve(node),
                ["label"] = ReadNodeString(node, "label"),
                ["automationId"] = ReadNodeString(node, "automationId"),
                ["depth"] = depth,
                ["bounds"] = bounds.ToPayload()
            };
        }

        if (node["children"] is not JsonArray children)
        {
            yield break;
        }

        foreach (var child in children.OfType<JsonObject>())
        {
            foreach (var target in BuildVisualTreeTapTargetCandidates(
                         visualTree,
                         child,
                         point,
                         surface,
                         typeRegistry,
                         depth + 1))
            {
                yield return target;
            }
        }
    }

    private static TouchSurface ResolveVisualTreeSurface(AppSessionSnapshot snapshot, SessionVisualTreeSnapshot visualTree)
    {
        var coordinateSpace = LiveUiNodeQuery.ReadCoordinateSpace(visualTree.Payload);
        if (coordinateSpace is { Width: > 0, Height: > 0 })
        {
            return new TouchSurface(
                coordinateSpace.X,
                coordinateSpace.Y,
                coordinateSpace.Width,
                coordinateSpace.Height);
        }

        var rootBounds = visualTree.Payload["root"] is JsonObject root
            ? LiveUiNodeQuery.ReadBounds(root)
            : null;
        if (rootBounds is { X: 0, Y: 0, Width: > 1, Height: > 1 })
        {
            return new TouchSurface(0, 0, rootBounds.Width, rootBounds.Height);
        }

        var frame = visualTree.ScreenshotFrameId is null
            ? null
            : snapshot.Images.FirstOrDefault(image =>
                string.Equals(image.FrameId, visualTree.ScreenshotFrameId, StringComparison.Ordinal));
        return frame is null
            ? TouchSurface.Empty
            : new TouchSurface(0, 0, frame.Width, frame.Height);
    }

    private static bool TryReadNodeBounds(JsonObject node, TouchSurface surface, out NormalizedBounds bounds)
    {
        bounds = default;
        if (node["normalizedBounds"] is JsonObject normalizedBounds
            && TryReadBoundsObject(normalizedBounds, surface, assumeNormalized: true, out bounds))
        {
            return true;
        }

        if (node["bounds"] is JsonArray compactBounds
            && TryReadBoundsArray(compactBounds, surface, out bounds))
        {
            return true;
        }

        return node["bounds"] is JsonObject boundsObject
               && TryReadBoundsObject(boundsObject, surface, assumeNormalized: false, out bounds);
    }

    private static bool TryReadBoundsArray(JsonArray boundsArray, TouchSurface surface, out NormalizedBounds bounds)
    {
        bounds = default;
        var xIndex = boundsArray.Count >= 8 ? 4 : 0;
        var yIndex = boundsArray.Count >= 8 ? 5 : 1;
        var widthIndex = boundsArray.Count >= 8 ? 6 : 2;
        var heightIndex = boundsArray.Count >= 8 ? 7 : 3;
        return TryReadDouble(boundsArray.ElementAtOrDefault(xIndex), out var x)
               && TryReadDouble(boundsArray.ElementAtOrDefault(yIndex), out var y)
               && TryReadDouble(boundsArray.ElementAtOrDefault(widthIndex), out var width)
               && TryReadDouble(boundsArray.ElementAtOrDefault(heightIndex), out var height)
               && TryNormalizeBounds(x, y, width, height, surface, out bounds);
    }

    private static bool TryReadBoundsObject(JsonObject boundsObject, TouchSurface surface, bool assumeNormalized, out NormalizedBounds bounds)
    {
        bounds = default;
        var xNode = boundsObject["absoluteX"] ?? boundsObject["x"];
        var yNode = boundsObject["absoluteY"] ?? boundsObject["y"];
        var widthNode = boundsObject["absoluteWidth"] ?? boundsObject["width"];
        var heightNode = boundsObject["absoluteHeight"] ?? boundsObject["height"];
        if (!TryReadDouble(xNode, out var x)
            || !TryReadDouble(yNode, out var y)
            || !TryReadDouble(widthNode, out var width)
            || !TryReadDouble(heightNode, out var height))
        {
            return false;
        }

        return assumeNormalized
            ? TryCreateNormalizedBounds(x, y, width, height, out bounds)
            : TryNormalizeBounds(x, y, width, height, surface, out bounds);
    }

    private static bool TryNormalizeBounds(double x, double y, double width, double height, TouchSurface surface, out NormalizedBounds bounds)
    {
        if (x >= 0d && y >= 0d && width > 0d && height > 0d && x <= 1d && y <= 1d && width <= 1d && height <= 1d)
        {
            return TryCreateNormalizedBounds(x, y, width, height, out bounds);
        }

        if (surface.Width.GetValueOrDefault() <= 0d || surface.Height.GetValueOrDefault() <= 0d)
        {
            bounds = default;
            return false;
        }

        return TryCreateNormalizedBounds(
            (x - surface.X) / surface.Width!.Value,
            (y - surface.Y) / surface.Height!.Value,
            width / surface.Width.Value,
            height / surface.Height.Value,
            out bounds);
    }

    private static bool TryCreateNormalizedBounds(double x, double y, double width, double height, out NormalizedBounds bounds)
    {
        bounds = default;
        if (double.IsNaN(x)
            || double.IsNaN(y)
            || double.IsNaN(width)
            || double.IsNaN(height)
            || width <= 0d
            || height <= 0d)
        {
            return false;
        }

        bounds = new NormalizedBounds(
            Math.Clamp(x, 0d, 1d),
            Math.Clamp(y, 0d, 1d),
            Math.Clamp(width, 0d, 1d),
            Math.Clamp(height, 0d, 1d));
        return true;
    }

    private static string? ReadNodeString(JsonObject node, string propertyName)
    {
        return node[propertyName]?.GetValue<string>();
    }

    private static bool TryReadDouble(JsonNode? node, out double value)
    {
        value = 0d;
        if (node is not JsonValue jsonValue)
        {
            return false;
        }

        return jsonValue.TryGetValue<double>(out value)
               || double.TryParse(jsonValue.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }
}
