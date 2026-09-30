namespace Ansight.Cli.Commands.Analytics;

internal sealed record AnalyticsStatusOutput(
    string Schema,
    bool DailyUseTrackingEnabled,
    bool DetailedTrackingEnabled,
    string Action,
    string SettingsPath);
