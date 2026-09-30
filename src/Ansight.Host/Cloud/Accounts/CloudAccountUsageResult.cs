using Ansight.Host.Trends;

namespace Ansight.Host.Cloud;

public sealed record CloudAccountUsageResult(
    bool IsSuccess,
    string Message,
    CloudAccountUsage? Usage)
{
    public static CloudAccountUsageResult Success(CloudAccountUsage usage)
        => new(true, $"Loaded AI usage for organisation '{usage.TeamName}'.", usage);

    public static CloudAccountUsageResult Failure(string message)
        => new(false, message, null);
}
