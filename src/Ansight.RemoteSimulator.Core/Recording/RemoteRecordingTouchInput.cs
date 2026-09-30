namespace Ansight.RemoteSimulator.Core.Recording;

public sealed record RemoteRecordingTouchInput(
    string Id,
    string Action,
    DateTimeOffset CapturedAtUtc,
    long PointerId,
    int PointerIndex,
    int PointerCount,
    double X,
    double Y,
    double? NormalizedX,
    double? NormalizedY,
    double? SurfaceWidth,
    double? SurfaceHeight,
    string CoordinateSpace,
    string CoordinateUnit,
    double? SurfaceScale);
