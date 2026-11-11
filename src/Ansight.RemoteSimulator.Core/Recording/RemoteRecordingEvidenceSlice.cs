namespace Ansight.RemoteSimulator.Core.Recording;

public sealed record RemoteRecordingEvidenceSlice(
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    IReadOnlyList<RemoteRecordingLogEntry> Logs,
    IReadOnlyList<RemoteRecordingAnnotation> Annotations,
    IReadOnlyList<RemoteRecordingMetricChannel> MetricChannels,
    IReadOnlyList<RemoteRecordingMetricSample> TelemetrySamples,
    bool LogsTruncated,
    bool TelemetryTruncated)
{
    public IReadOnlyList<RemoteRecordingEvent> Events { get; init; } = [];

    public bool EventsTruncated { get; init; }

    public IReadOnlyList<RemoteRecordingTouchInput> Touches { get; init; } = [];
}

public sealed record RemoteRecordingEvent(
    string Id,
    string Label,
    string EventType,
    string Details,
    DateTimeOffset CapturedAtUtc,
    int ChannelId);
