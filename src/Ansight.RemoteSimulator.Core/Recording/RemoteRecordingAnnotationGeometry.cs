namespace Ansight.RemoteSimulator.Core.Recording;

public sealed record RemoteRecordingAnnotationGeometry(
    string Id,
    string FrameId,
    DateTimeOffset CapturedAtUtc,
    string Kind,
    double X,
    double Y,
    double? Width,
    double? Height,
    IReadOnlyList<RemoteRecordingAnnotationPoint> Points,
    string? Text,
    string? StrokeColor,
    double? StrokeWidth);
