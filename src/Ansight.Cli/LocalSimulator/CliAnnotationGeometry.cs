using Ansight.RemoteSimulator.Core.Annotations;

namespace Ansight.Cli.LocalSimulator;

internal static class CliAnnotationGeometry
{
    public static AnnotationGeometryResult Create(
        RemoteAnnotationRequest request,
        string frameId,
        DateTimeOffset capturedAtUtc,
        IReadOnlyList<SessionAnnotationGeometry>? existing = null)
    {
        var kind = request.Kind?.ToLowerInvariant() switch
        {
            "point" => SessionAnnotationGeometryKind.Point,
            "rectangle" => SessionAnnotationGeometryKind.Rectangle,
            "freedraw" => SessionAnnotationGeometryKind.FreeDraw,
            _ => (SessionAnnotationGeometryKind?)null
        };

        if (kind is null)
        {
            return Failure("Choose Draw, Point or Region.");
        }

        var geometry = new List<SessionAnnotationGeometry>();

        if (kind == SessionAnnotationGeometryKind.FreeDraw)
        {
            if (request.Strokes is null || request.Strokes.Count is < 1 or > 16)
            {
                return Failure("A drawing must contain between 1 and 16 strokes.");
            }

            var totalPoints = 0;

            foreach (var stroke in request.Strokes)
            {
                if (stroke is null || stroke.Points is null)
                {
                    return Failure("Each stroke must contain between 1 and 256 points.");
                }

                if (stroke.Points.Count is < 1 or > 256)
                {
                    return Failure("Each stroke must contain between 1 and 256 points.");
                }

                totalPoints += stroke.Points.Count;

                if (totalPoints > 1024 || stroke.Points.Any(point => point is null || !IsCoordinate(point.X, point.Y)))
                {
                    return Failure("The drawing is too large or contains invalid coordinates.");
                }

                var points = stroke.Points.Select(point => new SessionAnnotationGeometryPoint
                {
                    X = point.X,
                    Y = point.Y
                }).ToArray();
                var x = points.Min(point => point.X);
                var y = points.Min(point => point.Y);
                var width = points.Max(point => point.X) - x;
                var height = points.Max(point => point.Y) - y;
                var isDot = points.Length == 1;
                var isDegenerateStroke = !isDot
                    && width == 0
                    && height == 0;

                if (isDegenerateStroke)
                {
                    return Failure("Draw a stroke or tap to add a dot.");
                }

                geometry.Add(new SessionAnnotationGeometry
                {
                    GeometryId = ExistingId(existing, geometry.Count),
                    FrameId = frameId,
                    CapturedAtUtc = capturedAtUtc,
                    Kind = isDot ? SessionAnnotationGeometryKind.Point : kind.Value,
                    X = x,
                    Y = y,
                    Width = isDot ? null : width,
                    Height = isDot ? null : height,
                    Points = isDot ? [] : points,
                    StrokeWidth = 3
                });
            }
        }
        else
        {
            if (!IsCoordinate(request.X, request.Y))
            {
                return Failure("The annotation coordinates are invalid.");
            }

            var width = request.Width.GetValueOrDefault();
            var height = request.Height.GetValueOrDefault();

            if (kind == SessionAnnotationGeometryKind.Rectangle)
            {
                var validSize = double.IsFinite(width)
                    && double.IsFinite(height)
                    && width > 0
                    && height > 0
                    && request.X + width <= 1.000001
                    && request.Y + height <= 1.000001;

                if (!validSize)
                {
                    return Failure("Drag a region inside the image.");
                }
            }

            geometry.Add(new SessionAnnotationGeometry
            {
                GeometryId = ExistingId(existing, 0),
                FrameId = frameId,
                CapturedAtUtc = capturedAtUtc,
                Kind = kind.Value,
                X = request.X,
                Y = request.Y,
                Width = kind == SessionAnnotationGeometryKind.Rectangle ? Math.Min(width, 1 - request.X) : null,
                Height = kind == SessionAnnotationGeometryKind.Rectangle ? Math.Min(height, 1 - request.Y) : null
            });
        }

        var left = geometry.Min(item => item.X);
        var top = geometry.Min(item => item.Y);
        var right = geometry.Max(item => item.X + item.Width.GetValueOrDefault());
        var bottom = geometry.Max(item => item.Y + item.Height.GetValueOrDefault());
        var bounds = new SessionAnnotationTargetBounds
        {
            X = left,
            Y = top,
            Width = right - left,
            Height = bottom - top
        };
        var isSinglePoint = right == left && bottom == top;

        if (kind == SessionAnnotationGeometryKind.Point || isSinglePoint)
        {
            const double pointSize = 0.018;
            bounds = new SessionAnnotationTargetBounds
            {
                X = Math.Clamp(left - pointSize / 2, 0, 1 - pointSize),
                Y = Math.Clamp(top - pointSize / 2, 0, 1 - pointSize),
                Width = pointSize,
                Height = pointSize
            };
        }

        return new AnnotationGeometryResult(null, bounds, geometry);
    }

    private static bool IsCoordinate(double x, double y)
    {
        var valid = double.IsFinite(x)
            && double.IsFinite(y)
            && x is >= 0 and <= 1
            && y is >= 0 and <= 1;

        return valid;
    }

    private static string ExistingId(IReadOnlyList<SessionAnnotationGeometry>? existing, int index)
    {
        return existing is not null && index < existing.Count
            ? existing[index].GeometryId
            : Guid.NewGuid().ToString("N");
    }

    private static AnnotationGeometryResult Failure(string message)
    {
        return new AnnotationGeometryResult(message, new SessionAnnotationTargetBounds(), []);
    }
}
