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
    IReadOnlyList<string> Tags)
{
    public string CaptureSource { get; init; } = "sdk";

    public System.Text.Json.Nodes.JsonObject? CustomProperties { get; init; }
    public string? ToolCatalogStatus { get; init; }
    public string? ToolCatalogMessage { get; init; }
    public int ToolCount { get; init; }
    public IReadOnlyList<SessionConnectionDiagnostic> ConnectionDiagnostics { get; init; } = [];
}

public sealed record SessionConnectionDiagnostic(string Code, string Message, string Recovery, string? Candidates);
