namespace Ansight.Host.Sessions;

public readonly record struct SessionNormalizationResult(
    bool IsSuccess,
    string Message,
    int RemovedScreenshotCount,
    int RemovedVisualTreeSnapshotCount)
{
    public int RemovedItemCount => RemovedScreenshotCount + RemovedVisualTreeSnapshotCount;

    public static SessionNormalizationResult Success(
        string message,
        int removedScreenshotCount,
        int removedVisualTreeSnapshotCount)
        => new(
            true,
            message,
            removedScreenshotCount,
            removedVisualTreeSnapshotCount);

    public static SessionNormalizationResult Failure(string message)
        => new(false, message, 0, 0);
}
