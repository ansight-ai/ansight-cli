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
    public SessionTouchSampleDetails? Details { get; init; }
}

public sealed class SessionTouchSampleDetails
{
    public string? Tool { get; init; }
    public string? SampleKind { get; init; }
    public double? Force { get; init; }
    public double? MaximumPossibleForce { get; init; }
    public double? AltitudeRadians { get; init; }
    public double? AzimuthRadians { get; init; }
    public double? RollRadians { get; init; }
    public long? EstimatedProperties { get; init; }
    public long? EstimatedPropertiesExpectingUpdates { get; init; }
    public long? EstimationUpdateIndex { get; init; }
    public double? Pressure { get; init; }
    public double? TiltRadians { get; init; }
    public double? OrientationRadians { get; init; }
    public double? Distance { get; init; }
    public int? ButtonState { get; init; }
    public double? TouchMajor { get; init; }
    public double? TouchMinor { get; init; }
    public double? ToolMajor { get; init; }
    public double? ToolMinor { get; init; }
}
