using Ansight.Host.Trends;

namespace Ansight.Host.Cloud;

public sealed record CloudRegisteredAppOperationResult(
    bool IsSuccess,
    string Message,
    CloudRegisteredApp? App)
{
    public static CloudRegisteredAppOperationResult Success(string message, CloudRegisteredApp? app = null)
        => new(true, message, app);

    public static CloudRegisteredAppOperationResult Failure(string message)
        => new(false, message, null);
}
