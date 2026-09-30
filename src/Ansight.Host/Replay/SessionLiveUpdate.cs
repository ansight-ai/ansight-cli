using AppLifecycleState = global::Ansight.AppLifecycleState;

namespace Ansight.Host.Replay;

public sealed record SessionLiveUpdate(
    string SessionId,
    bool RequiresReset,
    DateTimeOffset LastUpdatedUtc,
    string Status,
    AppLifecycleState AppState,
    DateTimeOffset? AppStateChangedUtc,
    IReadOnlyList<LogEntry> Logs,
    IReadOnlyList<SessionLogStream> LogStreams,
    IReadOnlyList<SessionImageFrame> Images,
    IReadOnlyList<SessionTouchInputRecord> Touches,
    IReadOnlyList<SessionNetworkRequest> NetworkRequests,
    IReadOnlyList<SessionVisualTreeSnapshot> VisualTreeSnapshots,
    IReadOnlyList<SessionArtifactSnapshot> ArtifactSnapshots,
    IReadOnlyList<SessionMetricChannel> MetricChannels,
    IReadOnlyList<SessionMetricSample> Metrics,
    IReadOnlyList<SessionAnnotation> Annotations,
    IReadOnlyList<SessionAnalysisRecord> Analyses,
    int TotalLogCount,
    int TotalImageCount,
    int TotalTouchCount,
    int TotalNetworkRequestCount,
    int TotalVisualTreeCount,
    int TotalArtifactCount,
    int TotalMetricChannelCount,
    int TotalMetricSampleCount,
    int TotalAnnotationCount,
    int TotalAnalysisCount)
{
    public IReadOnlyList<SessionApplicationEvent> LifecycleEvents { get; init; } = [];
    public System.Text.Json.Nodes.JsonObject? CustomProperties { get; init; }
}
