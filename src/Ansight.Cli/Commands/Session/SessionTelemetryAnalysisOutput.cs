namespace Ansight.Cli.Commands.Session;

internal sealed record SessionTelemetryAnalysisOutput(
    string Schema,
    string SessionId,
    string RequestedKind,
    int MetricSampleCount,
    int MaximumEventsPerKind,
    SessionMemorySpikeThresholdsOutput MemorySpikeThresholds,
    int DetectionCount,
    IReadOnlyList<SessionFpsDropDetectionOutput> FpsDrops,
    IReadOnlyList<SessionMemorySpikeDetectionOutput> MemorySpikes);
