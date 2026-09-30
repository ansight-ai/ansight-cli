namespace Ansight.Host.Runtime.Operations.Tools.TouchReview;

internal static class TouchReviewGeometry
{
    public static NormalizedPoint? ResolveGesturePoint(TouchGestureSegment gesture)
    {
        var upTouch = gesture.Touches.LastOrDefault(touch => IsTouchEnd(touch.Action));
        return ResolveTouchPoint(upTouch ?? gesture.Touches[^1]);
    }

    public static NormalizedPoint? ResolveTouchPoint(SessionTouchInputRecord touch)
    {
        if (!TryResolveNormalizedCoordinate(touch.NormalizedX, touch.X, touch.SurfaceWidth, touch.CoordinateUnit, out var x)
            || !TryResolveNormalizedCoordinate(touch.NormalizedY, touch.Y, touch.SurfaceHeight, touch.CoordinateUnit, out var y))
        {
            return null;
        }

        return new NormalizedPoint(x, y);
    }

    public static string NormalizeTouchAction(string? action)
    {
        return action?.Trim().ToLowerInvariant() switch
        {
            "pressed" or "began" => "down",
            "moved" => "move",
            "released" => "up",
            "cancelled" or "canceled" => "cancel",
            "down" or "move" or "up" or "cancel" => action.Trim().ToLowerInvariant(),
            _ => "unknown"
        };
    }

    public static bool IsTouchDown(string? action)
        => string.Equals(NormalizeTouchAction(action), "down", StringComparison.Ordinal);

    public static bool IsTouchEnd(string? action)
    {
        var normalizedAction = NormalizeTouchAction(action);
        return string.Equals(normalizedAction, "up", StringComparison.Ordinal)
               || string.Equals(normalizedAction, "cancel", StringComparison.Ordinal);
    }

    public static double CalculateDistance(NormalizedPoint left, NormalizedPoint right)
    {
        var dx = right.X - left.X;
        var dy = right.Y - left.Y;
        return Math.Sqrt((dx * dx) + (dy * dy));
    }

    public static long CalculateDistanceMilliseconds(DateTimeOffset timestampUtc, DateTimeOffset targetStartUtc, DateTimeOffset targetEndUtc)
    {
        if (timestampUtc >= targetStartUtc && timestampUtc <= targetEndUtc)
        {
            return 0;
        }

        var distance = timestampUtc < targetStartUtc
            ? targetStartUtc - timestampUtc
            : timestampUtc - targetEndUtc;
        return (long)Math.Round(distance.TotalMilliseconds);
    }

    public static bool AnnotationOverlaps(SessionAnnotation annotation, DateTimeOffset startUtc, DateTimeOffset endUtc)
    {
        var effectiveEndUtc = annotation.EndUtc ?? annotation.StartUtc;
        return effectiveEndUtc >= startUtc && annotation.StartUtc <= endUtc;
    }

    public static bool BoundsContains(SessionAnnotationTargetBounds bounds, NormalizedPoint point)
    {
        return point.X >= bounds.X
               && point.X <= bounds.X + bounds.Width
               && point.Y >= bounds.Y
               && point.Y <= bounds.Y + bounds.Height;
    }

    public static bool BoundsContains(NormalizedBounds bounds, NormalizedPoint point)
    {
        return point.X >= bounds.X
               && point.X <= bounds.X + bounds.Width
               && point.Y >= bounds.Y
               && point.Y <= bounds.Y + bounds.Height;
    }

    public static bool GeometryContains(SessionAnnotationGeometry geometry, NormalizedPoint point)
    {
        var width = geometry.Width.GetValueOrDefault();
        var height = geometry.Height.GetValueOrDefault();
        if (geometry.Kind == SessionAnnotationGeometryKind.Rectangle && width > 0d && height > 0d)
        {
            return point.X >= geometry.X
                   && point.X <= geometry.X + width
                   && point.Y >= geometry.Y
                   && point.Y <= geometry.Y + height;
        }

        if (geometry.Kind == SessionAnnotationGeometryKind.Ellipse && width > 0d && height > 0d)
        {
            var radiusX = width / 2d;
            var radiusY = height / 2d;
            var centerX = geometry.X + radiusX;
            var centerY = geometry.Y + radiusY;
            var normalizedX = (point.X - centerX) / radiusX;
            var normalizedY = (point.Y - centerY) / radiusY;
            return (normalizedX * normalizedX) + (normalizedY * normalizedY) <= 1d;
        }

        if (geometry.Kind == SessionAnnotationGeometryKind.FreeDraw && geometry.Points.Count > 1)
        {
            for (var index = 1; index < geometry.Points.Count; index++)
            {
                var start = new NormalizedPoint(geometry.Points[index - 1].X, geometry.Points[index - 1].Y);
                var end = new NormalizedPoint(geometry.Points[index].X, geometry.Points[index].Y);
                if (DistanceToSegment(point, start, end) <= TouchReviewDefaults.TapMaximumNormalizedDistance)
                {
                    return true;
                }
            }

            return false;
        }

        return CalculateDistance(new NormalizedPoint(geometry.X, geometry.Y), point) <= TouchReviewDefaults.TapMaximumNormalizedDistance;
    }

    private static double DistanceToSegment(NormalizedPoint point, NormalizedPoint start, NormalizedPoint end)
    {
        var segmentX = end.X - start.X;
        var segmentY = end.Y - start.Y;
        var lengthSquared = (segmentX * segmentX) + (segmentY * segmentY);
        if (lengthSquared <= double.Epsilon)
        {
            return CalculateDistance(point, start);
        }

        var projection = (((point.X - start.X) * segmentX) + ((point.Y - start.Y) * segmentY)) / lengthSquared;
        projection = Math.Clamp(projection, 0d, 1d);
        return CalculateDistance(
            point,
            new NormalizedPoint(start.X + (projection * segmentX), start.Y + (projection * segmentY)));
    }

    private static bool TryResolveNormalizedCoordinate(
        double? normalizedValue,
        double rawValue,
        double? surfaceLength,
        string coordinateUnit,
        out double normalized)
    {
        if (TryNormalizeUnitValue(normalizedValue, out normalized))
        {
            return true;
        }

        if (IsNormalizedCoordinateUnit(coordinateUnit) && TryNormalizeUnitValue(rawValue, out normalized))
        {
            return true;
        }

        var resolvedSurfaceLength = surfaceLength.GetValueOrDefault();
        if (resolvedSurfaceLength <= 0d)
        {
            normalized = 0d;
            return false;
        }

        normalized = Math.Clamp(rawValue / resolvedSurfaceLength, 0d, 1d);
        return true;
    }

    private static bool TryNormalizeUnitValue(double? value, out double normalized)
    {
        normalized = 0d;
        return value.HasValue && TryNormalizeUnitValue(value.Value, out normalized);
    }

    private static bool TryNormalizeUnitValue(double value, out double normalized)
    {
        normalized = 0d;
        if (double.IsNaN(value) || double.IsInfinity(value) || value < 0d || value > 1d)
        {
            return false;
        }

        normalized = value;
        return true;
    }

    private static bool IsNormalizedCoordinateUnit(string coordinateUnit)
    {
        return string.Equals(coordinateUnit?.Trim(), "normalized", StringComparison.OrdinalIgnoreCase)
               || string.Equals(coordinateUnit?.Trim(), "unit", StringComparison.OrdinalIgnoreCase)
               || string.Equals(coordinateUnit?.Trim(), "ratio", StringComparison.OrdinalIgnoreCase);
    }
}
