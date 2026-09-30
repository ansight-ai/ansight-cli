namespace Ansight.Host.Runtime.SessionCaptureStorage;

using AppLifecycleState = global::Ansight.AppLifecycleState;
using Ansight.Host;

internal sealed class SessionIncrementalPersistenceState
{
    public int PersistedLogCount { get; set; }
    public int PersistedMetricSampleCount { get; set; }
    public int PersistedAnalysisCount { get; set; }
    public int PersistedImageCount { get; set; }
    public int PersistedTouchInputCount { get; set; }
    public HashSet<string> PersistedImageFrameIds { get; init; } = new(StringComparer.Ordinal);
    public HashSet<string> PersistedTouchIds { get; init; } = new(StringComparer.Ordinal);
    public HashSet<string> PersistedNetworkRequestIds { get; init; } = new(StringComparer.Ordinal);
    public object? PersistedAnalysesSource { get; set; }
    public object? PersistedAnnotationsSource { get; set; }
    public object? PersistedAgentTaskLinksSource { get; set; }
    public object? PersistedImagesSource { get; set; }
    public object? PersistedTouchesSource { get; set; }
    public object? PersistedApplicationEventsSource { get; set; }
    public object? PersistedNetworkRequestsSource { get; set; }
    public PersistedSnapshotDocumentCollectionState PersistedVisualTreeSnapshots { get; init; } = new();
    public PersistedSnapshotDocumentCollectionState PersistedArtifactSnapshots { get; init; } = new();
    public object? PersistedMetricChannelsSource { get; set; }
    public object? PersistedMetricsSource { get; set; }
    public object? PersistedDeviceProfileSource { get; set; }
    public string? PersistedDeviceProfileJson { get; set; }
    public string? PersistedAppToolCatalogSchema { get; set; }
    public DateTimeOffset? PersistedAppToolCatalogCapturedAtUtc { get; set; }
}
