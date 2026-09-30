namespace Ansight.Analytics;

public sealed record AnalyticsSettings(bool DetailedTrackingEnabled, string? AccountId = null);
