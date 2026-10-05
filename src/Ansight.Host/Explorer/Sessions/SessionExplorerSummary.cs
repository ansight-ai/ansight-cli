namespace Ansight.Host.Replay;

public sealed record SessionExplorerSummary(
    string SessionId,
    string AppId,
    string? AppName,
    string? AppIconUrl,
    string? Name,
    string ClientName,
    string Status,
    bool IsConnected,
    bool IsSimulatorOrEmulator,
    string? RuntimeDeviceIdentifier,
    string? RuntimePlatform,
    string? Technology,
    bool IsHistorical,
    bool IsPinned,
    DateTimeOffset CreatedUtc,
    DateTimeOffset LastUpdatedUtc,
    int LogCount,
    int ScreenshotCount,
    int VisualTreeSnapshotCount,
    int ArtifactSnapshotCount,
    IReadOnlyList<string> Tags);
