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
    public IReadOnlyList<RemoteRecordingTouchInput> Touches { get; init; } = [];
}
