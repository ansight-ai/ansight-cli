using Ansight.Host.Trends;

namespace Ansight.Host.Cloud;

public sealed record CloudRunnerKeyQueryResult(
    bool IsSuccess,
    string Message,
    IReadOnlyList<CloudRunnerKeySummary> Keys)
{
    public static CloudRunnerKeyQueryResult Success(IReadOnlyList<CloudRunnerKeySummary> keys)
        => new(true, $"Found {keys.Count} runner API key(s).", keys);

    public static CloudRunnerKeyQueryResult Failure(string message)
        => new(false, message, []);
}
