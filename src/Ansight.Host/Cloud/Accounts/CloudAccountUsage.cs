using Ansight.Host.Trends;

namespace Ansight.Host.Cloud;

public sealed record CloudAccountUsage(
    Guid TeamId,
    string TeamName,
    CloudAiTokenBalance TokenBucket,
    CloudOpenAiBillingSummary OpenAiBilling)
{
    public bool HostedExecutionAvailable => OpenAiBilling.HasRemainingAllowance;

    public string? HostedExecutionBlockReason => OpenAiBilling.HostedExecutionBlockReason;
}
