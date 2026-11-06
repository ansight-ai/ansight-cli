namespace Ansight.Host.Replay;

/// <summary>
/// Identifies the captured evidence used to derive a replay step without making
/// capture-local node identifiers part of the live action contract.
/// </summary>
public sealed record ReplaySourceEvidence
{
    public string? VisualTreeSnapshotId { get; init; }
    public string? ScreenshotFrameId { get; init; }
    public double? StartNormalizedX { get; init; }
    public double? StartNormalizedY { get; init; }
    public double? EndNormalizedX { get; init; }
    public double? EndNormalizedY { get; init; }
    public long? DurationMilliseconds { get; init; }
}
