namespace Ansight.Host.Runtime.SessionCaptureStorage;

using System.Text.Json.Nodes;
using AppLifecycleState = global::Ansight.AppLifecycleState;
using Ansight.Host;

internal sealed class SessionCaptureSummary
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
    public SessionAppIcon? AppIcon { get; init; }
    public required bool HasDeviceProfile { get; init; }
    public required int AnalysisCount { get; init; }
    public int AnnotationCount { get; init; }
    public required int ImageCount { get; init; }
    public int TouchInputCount { get; init; }
    public int VisualTreeSnapshotCount { get; init; }
    public int ArtifactSnapshotCount { get; init; }
    public required int LogCount { get; init; }
    public required int MetricChannelCount { get; init; }
    public required int MetricSampleCount { get; init; }
    public int ApplicationEventCount { get; init; }
    public int NetworkRequestCount { get; init; }
}
