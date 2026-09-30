using Ansight.Host.Trends;

namespace Ansight.Host.Cloud;

public sealed record CloudOpenAiBillingSummary(
    bool PricingConfigured,
    string Currency,
    long RetailAllowanceMicros,
    long CustomerSpendMicros,
    long CustomerRemainingMicros,
    long InputTokens,
    long CachedInputTokens,
    long OutputTokens,
    long RequestCount,
    DateTimeOffset? PeriodStartUtc,
    DateTimeOffset? PeriodEndUtc,
    bool PeriodIsCurrent,
    string SyncStatus,
    string? SyncError,
    DateTimeOffset? SyncedAtUtc)
{
    public bool HasRemainingAllowance => PeriodIsCurrent
                                         && PricingConfigured
                                         && CustomerRemainingMicros > 0;

    public string? HostedExecutionBlockReason => HasRemainingAllowance
        ? null
        : !PricingConfigured
            ? "OpenAI billing is not configured for this organisation."
            : !PeriodIsCurrent
                ? PeriodEndUtc is { } periodEndUtc
                    ? $"No active OpenAI billing period. The latest period ended at {periodEndUtc:O}."
                    : "No active OpenAI billing period."
                : "The active OpenAI billing allowance has no remaining balance.";
}
