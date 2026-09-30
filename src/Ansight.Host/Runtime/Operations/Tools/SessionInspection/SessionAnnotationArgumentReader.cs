using System.Globalization;
using System.Text.Json.Nodes;

namespace Ansight.Host.Runtime.Operations.Tools.SessionInspection;

internal static class SessionAnnotationArgumentReader
{
    public static bool TryReadGeometries(
        JsonObject? arguments,
        DateTimeOffset annotationStartUtc,
        out IReadOnlyList<SessionAnnotationGeometry> geometries,
        out string? errorMessage)
    {
        geometries = Array.Empty<SessionAnnotationGeometry>();
        errorMessage = null;

        if (arguments?["geometries"] is null)
        {
            return true;
        }

        if (arguments["geometries"] is not JsonArray geometryArray)
        {
            errorMessage = "geometries must be an array.";
            return false;
        }

        var parsedGeometries = new List<SessionAnnotationGeometry>();
        for (var index = 0; index < geometryArray.Count; index++)
        {
            if (geometryArray[index] is null)
            {
                continue;
            }

            if (geometryArray[index] is not JsonObject geometryObject)
            {
                errorMessage = $"geometries[{index}] must be an object.";
                return false;
            }

            if (!TryReadGeometry(geometryObject, index, annotationStartUtc, out var geometry, out errorMessage))
            {
                return false;
            }

            parsedGeometries.Add(geometry!);
        }

        geometries = parsedGeometries.ToArray();
        return true;
    }

    public static bool TryReadTarget(
        JsonObject? arguments,
        string annotationSource,
        out SessionAnnotationTarget? target,
        out string? errorMessage)
    {
        target = null;
        errorMessage = null;
        if (arguments?["target"] is null)
        {
            return true;
        }

        if (arguments["target"] is not JsonObject targetObject)
        {
            errorMessage = "target must be an object.";
            return false;
        }

        if (!TryReadOptionalInteger(targetObject, "depth", out var depth, out errorMessage)
            || !TryReadOptionalInteger(targetObject, "childCount", out var childCount, out errorMessage)
            || !TryReadTargetBounds(targetObject, "absoluteBounds", out var absoluteBounds, out errorMessage)
            || !TryReadTargetBounds(targetObject, "normalizedBounds", out var normalizedBounds, out errorMessage))
        {
            return false;
        }

        target = new SessionAnnotationTarget
        {
            Kind = NormalizeOptionalString(targetObject["kind"]?.GetValue<string>()) ?? "agentAnnotation",
            Source = NormalizeOptionalString(targetObject["source"]?.GetValue<string>()) ?? annotationSource,
            TargetId = NormalizeOptionalString(targetObject["targetId"]?.GetValue<string>()) ?? $"agent-target-{Guid.NewGuid():N}",
            VisualTreeSnapshotId = NormalizeOptionalString(targetObject["visualTreeSnapshotId"]?.GetValue<string>()) ?? string.Empty,
            Type = NormalizeOptionalString(targetObject["type"]?.GetValue<string>()) ?? string.Empty,
            ElementKind = NormalizeOptionalString(targetObject["elementKind"]?.GetValue<string>()) ?? string.Empty,
            Label = NormalizeOptionalString(targetObject["label"]?.GetValue<string>()) ?? string.Empty,
            AutomationId = NormalizeOptionalString(targetObject["automationId"]?.GetValue<string>()) ?? string.Empty,
            Depth = depth,
            ChildCount = childCount,
            AbsoluteBounds = absoluteBounds,
            NormalizedBounds = normalizedBounds
        };
        return true;
    }

    private static bool TryReadGeometry(
        JsonObject geometryObject,
        int index,
        DateTimeOffset annotationStartUtc,
        out SessionAnnotationGeometry? geometry,
        out string? errorMessage)
    {
        geometry = null;
        var frameId = NormalizeOptionalString(geometryObject["frameId"]?.GetValue<string>());
        if (frameId is null)
        {
            errorMessage = $"geometries[{index}].frameId is required.";
            return false;
        }

        if (!TryReadOptionalDateTimeOffset(geometryObject, "capturedAtUtc", out var capturedAtUtc, out errorMessage)
            || !TryReadRequiredDouble(geometryObject, "x", out var x, out errorMessage)
            || !TryReadRequiredDouble(geometryObject, "y", out var y, out errorMessage)
            || !TryReadOptionalDouble(geometryObject, "width", out var width, out errorMessage)
            || !TryReadOptionalDouble(geometryObject, "height", out var height, out errorMessage)
            || !TryReadGeometryPoints(geometryObject, out var points, out errorMessage)
            || !TryReadOptionalDouble(geometryObject, "strokeWidth", out var strokeWidth, out errorMessage))
        {
            errorMessage = errorMessage is null ? null : $"geometries[{index}].{errorMessage}";
            return false;
        }

        var kindText = NormalizeOptionalString(geometryObject["kind"]?.GetValue<string>());
        var kind = width.HasValue && height.HasValue
            ? SessionAnnotationGeometryKind.Rectangle
            : SessionAnnotationGeometryKind.Point;
        if (kindText is not null
            && !Enum.TryParse<SessionAnnotationGeometryKind>(kindText, ignoreCase: true, out kind))
        {
            errorMessage = $"geometries[{index}].kind must be point, rectangle, ellipse, or freeDraw.";
            return false;
        }

        if ((kind is SessionAnnotationGeometryKind.Rectangle or SessionAnnotationGeometryKind.Ellipse)
            && (!width.HasValue || !height.HasValue))
        {
            errorMessage = $"geometries[{index}].width and height are required for rectangle and ellipse geometry.";
            return false;
        }

        if (kind == SessionAnnotationGeometryKind.FreeDraw && points.Count < 2)
        {
            errorMessage = $"geometries[{index}].points must contain at least two points for freeDraw geometry.";
            return false;
        }

        if (kind == SessionAnnotationGeometryKind.Point)
        {
            width = null;
            height = null;
        }

        geometry = new SessionAnnotationGeometry
        {
            GeometryId = NormalizeOptionalString(geometryObject["geometryId"]?.GetValue<string>()) ?? Guid.NewGuid().ToString("N"),
            FrameId = frameId,
            CapturedAtUtc = (capturedAtUtc ?? annotationStartUtc).ToUniversalTime(),
            Kind = kind,
            X = x,
            Y = y,
            Width = width,
            Height = height,
            Points = kind == SessionAnnotationGeometryKind.FreeDraw
                ? points
                : Array.Empty<SessionAnnotationGeometryPoint>(),
            Text = NormalizeOptionalString(geometryObject["text"]?.GetValue<string>()),
            StrokeColor = NormalizeOptionalString(geometryObject["strokeColor"]?.GetValue<string>()),
            StrokeWidth = strokeWidth
        };
        return true;
    }

    private static bool TryReadGeometryPoints(
        JsonObject geometryObject,
        out IReadOnlyList<SessionAnnotationGeometryPoint> points,
        out string? errorMessage)
    {
        points = Array.Empty<SessionAnnotationGeometryPoint>();
        errorMessage = null;
        if (geometryObject["points"] is null)
        {
            return true;
        }

        if (geometryObject["points"] is not JsonArray pointArray)
        {
            errorMessage = "points must be an array.";
            return false;
        }

        var parsedPoints = new List<SessionAnnotationGeometryPoint>();
        for (var index = 0; index < pointArray.Count; index++)
        {
            if (pointArray[index] is not JsonObject pointObject)
            {
                errorMessage = $"points[{index}] must be an object.";
                return false;
            }

            if (!TryReadRequiredDouble(pointObject, "x", out var x, out errorMessage)
                || !TryReadRequiredDouble(pointObject, "y", out var y, out errorMessage))
            {
                errorMessage = errorMessage is null ? null : $"points[{index}].{errorMessage}";
                return false;
            }

            parsedPoints.Add(new SessionAnnotationGeometryPoint
            {
                X = x,
                Y = y
            });
        }

        points = parsedPoints;
        return true;
    }

    private static bool TryReadTargetBounds(
        JsonObject targetObject,
        string propertyName,
        out SessionAnnotationTargetBounds? bounds,
        out string? errorMessage)
    {
        bounds = null;
        errorMessage = null;
        if (targetObject[propertyName] is null)
        {
            return true;
        }

        if (targetObject[propertyName] is not JsonObject boundsObject)
        {
            errorMessage = $"{propertyName} must be an object.";
            return false;
        }

        if (!TryReadRequiredDouble(boundsObject, "x", out var x, out errorMessage)
            || !TryReadRequiredDouble(boundsObject, "y", out var y, out errorMessage)
            || !TryReadRequiredDouble(boundsObject, "width", out var width, out errorMessage)
            || !TryReadRequiredDouble(boundsObject, "height", out var height, out errorMessage))
        {
            errorMessage = errorMessage is null ? null : $"{propertyName}.{errorMessage}";
            return false;
        }

        bounds = new SessionAnnotationTargetBounds
        {
            X = x,
            Y = y,
            Width = width,
            Height = height
        };
        return true;
    }

    private static bool TryReadOptionalDateTimeOffset(
        JsonObject jsonObject,
        string propertyName,
        out DateTimeOffset? value,
        out string? errorMessage)
    {
        value = null;
        errorMessage = null;
        if (jsonObject[propertyName] is null)
        {
            return true;
        }

        var text = jsonObject[propertyName]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        if (DateTimeOffset.TryParse(text, out var parsed))
        {
            value = parsed;
            return true;
        }

        errorMessage = $"{propertyName} must be a valid ISO-8601 timestamp.";
        return false;
    }

    private static bool TryReadOptionalInteger(
        JsonObject jsonObject,
        string propertyName,
        out int value,
        out string? errorMessage)
    {
        value = 0;
        errorMessage = null;
        if (jsonObject[propertyName] is null)
        {
            return true;
        }

        if (jsonObject[propertyName] is JsonValue jsonValue
            && (jsonValue.TryGetValue<int>(out value)
                || int.TryParse(jsonValue.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value)))
        {
            return true;
        }

        errorMessage = $"{propertyName} must be an integer.";
        return false;
    }

    private static bool TryReadRequiredDouble(
        JsonObject jsonObject,
        string propertyName,
        out double value,
        out string? errorMessage)
    {
        if (jsonObject[propertyName] is null)
        {
            value = 0d;
            errorMessage = $"{propertyName} is required.";
            return false;
        }

        return TryReadDouble(jsonObject, propertyName, out value, out errorMessage);
    }

    private static bool TryReadOptionalDouble(
        JsonObject jsonObject,
        string propertyName,
        out double? value,
        out string? errorMessage)
    {
        value = null;
        errorMessage = null;
        if (jsonObject[propertyName] is null)
        {
            return true;
        }

        if (!TryReadDouble(jsonObject, propertyName, out var parsed, out errorMessage))
        {
            return false;
        }

        value = parsed;
        return true;
    }

    private static bool TryReadDouble(
        JsonObject jsonObject,
        string propertyName,
        out double value,
        out string? errorMessage)
    {
        value = 0d;
        errorMessage = null;
        if (jsonObject[propertyName] is JsonValue jsonValue
            && (jsonValue.TryGetValue<double>(out value)
                || double.TryParse(jsonValue.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            && double.IsFinite(value))
        {
            return true;
        }

        errorMessage = $"{propertyName} must be a finite number.";
        return false;
    }

    private static string? NormalizeOptionalString(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
