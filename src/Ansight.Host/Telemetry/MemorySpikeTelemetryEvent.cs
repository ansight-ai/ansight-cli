namespace Ansight.Host.Telemetry;

public sealed record MemorySpikeTelemetryEvent(
    byte ChannelId,
    string ChannelName,
    SessionMetricSample Baseline,
    SessionMetricSample Peak,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    long DeltaBytes,
    double DeltaPercent,
    DateTimeOffset? RetainedUntilUtc,
    TelemetryAnalysisSeverity Severity,
    double SeverityScore);
