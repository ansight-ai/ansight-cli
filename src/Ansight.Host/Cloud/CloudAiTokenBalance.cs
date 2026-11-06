using Ansight.Host.Trends;

namespace Ansight.Host.Cloud;

public sealed record CloudAiTokenBalance(
    long AllocatedTokens,
    long ConsumedTokens,
    long RemainingTokens,
    DateTimeOffset? UpdatedAtUtc);
