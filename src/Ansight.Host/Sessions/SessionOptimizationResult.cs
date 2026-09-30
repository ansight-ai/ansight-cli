namespace Ansight.Host.Sessions;

public sealed record SessionOptimizationProgress(string Message, int? Completed = null, int? Total = null);

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
