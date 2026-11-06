namespace Ansight.Host.Sessions;

public sealed record SessionOptimizationResult(
    bool IsSuccess,
    string Message,
    int RemovedScreenshotCount,
    int RemovedVisualTreeSnapshotCount,
    int ConvertedImageCount,
    string? ArchiveFilePath = null)
{
    public int RemovedItemCount => RemovedScreenshotCount + RemovedVisualTreeSnapshotCount;
}
