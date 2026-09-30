namespace Ansight.Host.Cloud.Trends;

public interface ITrendsOperations
{
    Task<CloudTrendsUploadResult> UploadTrendsHistoryAsync(
        CloudTrendsUploadRequest request,
        CancellationToken cancellationToken = default);
}
