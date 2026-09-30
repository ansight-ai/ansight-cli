namespace Ansight.Host.Telemetry;

public sealed record FpsDropTelemetryEvent(
    byte ChannelId,
    string ChannelName,
    SessionMetricSample Baseline,
    SessionMetricSample Minimum,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    long DropFps,
    TelemetryAnalysisSeverity Severity,
    double SeverityScore);
