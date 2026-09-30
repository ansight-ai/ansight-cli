namespace Ansight.RemoteSimulator.Core.Recording;

public sealed record RemoteRecordingMetricSample(
    int ChannelId,
    long Value,
    DateTimeOffset CapturedAtUtc,
    int SegmentId);
