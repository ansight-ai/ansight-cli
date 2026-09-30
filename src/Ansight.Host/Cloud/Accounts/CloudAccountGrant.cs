using Ansight.Host.Trends;

namespace Ansight.Host.Cloud;

public sealed record CloudAccountGrant(
    Guid TeamId,
    string TeamName,
    string? TeamRole,
    string Plan,
    string AccessStatus,
    bool IsCurrent,
    DateTimeOffset? StartsAtUtc,
    DateTimeOffset? TrialStartedAtUtc,
    DateTimeOffset? TrialEndsAtUtc,
    DateTimeOffset? CurrentPeriodEndUtc,
    DateTimeOffset? ExpiresAtUtc,
    long? RemainingSeconds,
    int RetentionDays,
    bool RetentionIsUnlimited,
    long StorageLimitBytes,
    bool CloudSessionShareEnabled,
    bool CloudTrendsEnabled,
    bool CloudTestTrackingEnabled,
    bool HostedComputationEnabled,
    DateTimeOffset UpdatedAtUtc);
