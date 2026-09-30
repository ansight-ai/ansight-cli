using Ansight.Host.Trends;

namespace Ansight.Host.Cloud;

public sealed record CloudTestRunUploadResult(
    bool IsSuccess,
    string Message,
    Guid? RunId)
{
    public static CloudTestRunUploadResult Success(Guid runId)
        => new(true, $"Uploaded test run {runId:D}.", runId);

    public static CloudTestRunUploadResult Failure(string message)
        => new(false, message, null);
}
