namespace Ansight.Host.Cloud;

public sealed record CloudSessionUploadRequest(
    Guid TeamId,
    string AccessStatus,
    AppSessionSnapshot Session,
    string ArchiveFilePath)
{
    public IProgress<CloudSessionStreamUploadProgress>? Progress { get; init; }

    public Action<SessionOptimizationProgress>? PreparationProgress { get; init; }

    public bool EncodeVideo { get; init; }

    public bool DeferUploadNotification { get; init; }
}
