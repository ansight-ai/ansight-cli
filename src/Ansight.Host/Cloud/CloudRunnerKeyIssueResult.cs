using Ansight.Host.Trends;

namespace Ansight.Host.Cloud;

public sealed record CloudRunnerKeyIssueResult(
    bool IsSuccess,
    string Message,
    CloudRunnerKeySummary? Key,
    string? ApiKey)
{
    public static CloudRunnerKeyIssueResult Success(CloudRunnerKeySummary key, string apiKey)
        => new(
            true,
            "Runner API key issued. Store it now; it cannot be retrieved again.",
            key,
            apiKey);

    public static CloudRunnerKeyIssueResult Failure(string message)
        => new(false, message, null, null);
}
