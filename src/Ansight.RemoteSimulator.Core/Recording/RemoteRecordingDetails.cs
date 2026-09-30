namespace Ansight.RemoteSimulator.Core.Recording;

public sealed record RemoteRecordingDetails(
    RemoteRecordingSummary Recording,
    IReadOnlyList<RemoteRecordingFrameSummary> Frames,
    IReadOnlyList<RemoteRecordingLogStreamSummary> LogStreams,
    IReadOnlyList<RemoteRecordingMetricChannel> MetricChannels);
