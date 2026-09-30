namespace Ansight.Host.Models.Session;

public sealed class SessionTouchInputRecord
{
    public required string Id { get; init; }
    public required string Action { get; init; }
    public required DateTimeOffset CapturedAtUtc { get; init; }
    public required long PointerId { get; init; }
    public required int PointerIndex { get; init; }
    public required int PointerCount { get; init; }
    public required double X { get; init; }
    public required double Y { get; init; }
    public double? NormalizedX { get; init; }
    public double? NormalizedY { get; init; }
    public double? SurfaceWidth { get; init; }
    public double? SurfaceHeight { get; init; }
    public string CoordinateSpace { get; init; } = "window";
    public required string CoordinateUnit { get; init; }
    public double? SurfaceScale { get; init; }
}
