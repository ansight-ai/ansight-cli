namespace Ansight.RemoteSimulator.Core.Recording;

public sealed record RemoteRecordingFrameSummary(
    string Id,
    DateTimeOffset CapturedAtUtc,
    int Width,
    int Height,
    string ContentType,
    long ByteCount);
