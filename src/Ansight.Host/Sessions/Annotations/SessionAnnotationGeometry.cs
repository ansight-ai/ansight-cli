namespace Ansight.Host.Models.Session;

public sealed class SessionAnnotationGeometry
{
    public required string GeometryId { get; init; }

    public required string FrameId { get; init; }

    public required DateTimeOffset CapturedAtUtc { get; init; }

    public required SessionAnnotationGeometryKind Kind { get; init; }

    public required double X { get; init; }

    public required double Y { get; init; }

    public double? Width { get; init; }

    public double? Height { get; init; }

    public IReadOnlyList<SessionAnnotationGeometryPoint> Points { get; init; } = Array.Empty<SessionAnnotationGeometryPoint>();

    public string? Text { get; init; }

    public string? StrokeColor { get; init; }

    public double? StrokeWidth { get; init; }
}
