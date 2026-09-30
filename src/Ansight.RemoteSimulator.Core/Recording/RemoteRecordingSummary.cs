namespace Ansight.RemoteSimulator.Core.Recording;

public sealed record RemoteRecordingSummary(
    string Id,
    string Title,
    string AppName,
    string DeviceName,
    string Platform,
    string Status,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset EndedAtUtc,
    long DurationMilliseconds,
    int FrameCount,
    bool IsLive,
    int LogCount = 0,
    int AnnotationCount = 0,
    int MetricChannelCount = 0,
    int MetricSampleCount = 0)
{
    public string AppId { get; init; } = string.Empty;

    public string? AppIconCacheKey { get; init; }

    public RemoteRecordingCapabilities Capabilities { get; } = new(
        HasScreenReplay: FrameCount > 0,
        HasLogs: LogCount > 0,
        HasAnnotations: AnnotationCount > 0,
        HasTelemetry: MetricChannelCount > 0 && MetricSampleCount > 0);
}
