namespace Ansight.Host.Cloud.Tests;

public interface ITestRunOperations
{
    Task<CloudTestRunUploadResult> UploadTestRunAsync(
        CloudTestRunUploadRequest request,
        CancellationToken cancellationToken = default);
}
