namespace Ansight.Host.Models.Session;

using System.Text.Json.Nodes;
using AppLifecycleState = global::Ansight.AppLifecycleState;
using Ansight.Pairing.Models;

public sealed class AppSessionSnapshot
{
    public required string SessionId { get; init; }
    public required string AppId { get; init; }
    public required string ClientName { get; init; }
    public required string RemoteAddress { get; init; }
    public string? Name { get; init; }
    public required DateTimeOffset CreatedUtc { get; init; }
    public required string? ConfigId { get; init; }
    public string? ProcessSessionId { get; init; }
    public required string Status { get; init; }
    public required DateTimeOffset LastUpdatedUtc { get; init; }
    public required bool IsHistorical { get; init; }
    public long CacheSizeBytes { get; init; }
    public bool IsPinned { get; init; }
    public SessionCaptureAuthorMetadata? Author { get; init; }
    public SessionReplaySource? ReplaySource { get; init; }
    public string CaptureSource { get; init; } = WorkspaceExecutionModes.Sdk;
    public string? SdkVersion { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();
    public string? Notes { get; init; }
    public JsonObject? CustomProperties { get; init; }
    public AppLifecycleState AppState { get; init; } = AppLifecycleState.Unknown;
    public DateTimeOffset? AppStateChangedUtc { get; init; }
    public DeviceAppProfile? DeviceProfile { get; init; }
    public string? DeviceProfileJson { get; init; }
    public SessionAppIcon? AppIcon { get; init; }
    public SessionAppToolCatalogSnapshot? AppToolCatalog { get; init; }
    public IReadOnlyList<SessionAnalysisRecord> Analyses { get; init; } = Array.Empty<SessionAnalysisRecord>();
    public IReadOnlyList<SessionAnnotation> Annotations { get; init; } = Array.Empty<SessionAnnotation>();
    public IReadOnlyList<SessionAgentTaskLink> AgentTaskLinks { get; init; } = Array.Empty<SessionAgentTaskLink>();
    public IReadOnlyList<SessionImageFrame> Images { get; init; } = Array.Empty<SessionImageFrame>();
    public IReadOnlyList<SessionTouchInputRecord> Touches { get; init; } = Array.Empty<SessionTouchInputRecord>();
    public IReadOnlyList<SessionNetworkRequest> NetworkRequests { get; init; } = Array.Empty<SessionNetworkRequest>();
    public IReadOnlyList<SessionVisualTreeSnapshot> VisualTreeSnapshots { get; init; } = Array.Empty<SessionVisualTreeSnapshot>();
    public IReadOnlyList<SessionArtifactSnapshot> ArtifactSnapshots { get; init; } = Array.Empty<SessionArtifactSnapshot>();
    public IReadOnlyList<SessionApplicationEvent> ApplicationEvents { get; init; } = Array.Empty<SessionApplicationEvent>();
    public IReadOnlyList<SessionLogStream> LogStreams { get; init; } = Array.Empty<SessionLogStream>();
    public IReadOnlyList<LogEntry> Logs { get; init; } = Array.Empty<LogEntry>();
    public int TotalLogCount { get; init; }
    public int RetainedLogStartIndex { get; init; }
    public required IReadOnlyList<SessionMetricChannel> MetricChannels { get; init; }
    public required IReadOnlyList<SessionMetricSample> Metrics { get; init; }
    public int TotalAnnotationCount { get; init; }
    public int TotalImageCount { get; init; }
    public int TotalMetricChannelCount { get; init; }
    public int TotalMetricSampleCount { get; init; }
    public int TotalApplicationEventCount { get; init; }
    public int TotalNetworkRequestCount { get; init; }
}

public sealed record SessionAppToolCatalogSnapshot(
    string Schema,
    DateTimeOffset CapturedAtUtc,
    JsonObject ToolCatalog,
    JsonObject? ArtifactCatalog);
