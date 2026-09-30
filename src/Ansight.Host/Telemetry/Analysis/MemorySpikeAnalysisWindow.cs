namespace Ansight.Host.Telemetry.Analysis;

internal sealed record MemorySpikeAnalysisWindow(
    byte ChannelId,
    SessionMetricSample Baseline,
    SessionMetricSample Peak,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    long DeltaBytes,
    DateTimeOffset? RetainedUntilUtc);
