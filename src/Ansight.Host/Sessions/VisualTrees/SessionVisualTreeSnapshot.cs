namespace Ansight.Host.Models.Session;

using System.Text.Json.Nodes;

public sealed class SessionVisualTreeSnapshot
{
    public required string SnapshotId { get; init; }

    public required DateTimeOffset CapturedAtUtc { get; init; }

    public string VisualTreeKind { get; init; } = string.Empty;

    public string VisualTreeFormat { get; init; } = string.Empty;

    public string RuntimePlatform { get; init; } = string.Empty;

    public required string Source { get; init; }

    public string RootScope { get; init; } = string.Empty;

    public int MaxDepth { get; init; }

    public bool IncludeProperties { get; init; }

    public bool IncludeBindableProperties { get; init; }

    public required int NodeCount { get; init; }

    public bool Truncated { get; init; }

    public string? ScreenshotFrameId { get; init; }

    public DateTimeOffset? ScreenshotCapturedAtUtc { get; init; }

    public string? ActionId { get; init; }

    public string? ActionCapability { get; init; }

    public string? EvidencePhase { get; init; }

    public string? TreeHash { get; init; }

    public string? ScreenshotHash { get; init; }

    public JsonObject Payload { get; init; } = new();
}
