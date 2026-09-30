namespace Ansight.Host.Telemetry.Analysis;

internal sealed record FpsDropAnalysisWindow(
    byte ChannelId,
    SessionMetricSample Baseline,
    SessionMetricSample Minimum,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc);
