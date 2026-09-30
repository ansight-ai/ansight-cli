namespace Ansight.Host.Replay;

public sealed record LiveVisualTreeCaptureResult(
    bool IsSuccess,
    string Message,
    string SessionId,
    string? SnapshotId,
    int NodeCount);
