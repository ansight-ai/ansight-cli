namespace Ansight.Host.Cloud;

public readonly record struct CloudSessionUploadResult(
    bool IsSuccess,
    string Message,
    CloudSessionSummary? Session)
{
    public static CloudSessionUploadResult Success(CloudSessionSummary session)
        => new(true, string.Empty, session);

    public static CloudSessionUploadResult Failure(string message)
        => new(false, string.IsNullOrWhiteSpace(message) ? "Unable to share session." : message.Trim(), null);
}
