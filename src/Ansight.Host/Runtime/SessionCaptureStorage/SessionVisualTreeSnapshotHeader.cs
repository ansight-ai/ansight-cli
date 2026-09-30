namespace Ansight.Host.Runtime.SessionCaptureStorage;

using AppLifecycleState = global::Ansight.AppLifecycleState;
using Ansight.Host;

internal sealed class SessionVisualTreeSnapshotHeader
{
    public required string SnapshotId { get; init; }
    public required DateTimeOffset CapturedAtUtc { get; init; }
    public required string VisualTreeKind { get; init; }
    public required string VisualTreeFormat { get; init; }
    public required string RuntimePlatform { get; init; }
    public required string Source { get; init; }
    public string RootScope { get; init; } = string.Empty;
    public int MaxDepth { get; init; }
    public bool IncludeProperties { get; init; }
    public bool IncludeBindableProperties { get; init; }
    public int NodeCount { get; init; }
    public bool Truncated { get; init; }
    public string? ScreenshotFrameId { get; init; }
    public DateTimeOffset? ScreenshotCapturedAtUtc { get; init; }
    public string? ActionId { get; init; }
    public string? ActionCapability { get; init; }
    public string? EvidencePhase { get; init; }
    public string? TreeHash { get; init; }
    public string? ScreenshotHash { get; init; }

    public static SessionVisualTreeSnapshotHeader Create(SessionVisualTreeSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return new SessionVisualTreeSnapshotHeader
        {
            SnapshotId = snapshot.SnapshotId,
            CapturedAtUtc = snapshot.CapturedAtUtc,
            VisualTreeKind = string.IsNullOrWhiteSpace(snapshot.VisualTreeKind) ? "unknown" : snapshot.VisualTreeKind,
            VisualTreeFormat = snapshot.VisualTreeFormat ?? string.Empty,
            RuntimePlatform = snapshot.RuntimePlatform ?? string.Empty,
            Source = snapshot.Source,
            RootScope = snapshot.RootScope,
            MaxDepth = snapshot.MaxDepth,
            IncludeProperties = snapshot.IncludeProperties,
            IncludeBindableProperties = snapshot.IncludeBindableProperties,
            NodeCount = snapshot.NodeCount,
            Truncated = snapshot.Truncated,
            ScreenshotFrameId = snapshot.ScreenshotFrameId,
            ScreenshotCapturedAtUtc = snapshot.ScreenshotCapturedAtUtc,
            ActionId = snapshot.ActionId,
            ActionCapability = snapshot.ActionCapability,
            EvidencePhase = snapshot.EvidencePhase,
            TreeHash = snapshot.TreeHash,
            ScreenshotHash = snapshot.ScreenshotHash
        };
    }
}
