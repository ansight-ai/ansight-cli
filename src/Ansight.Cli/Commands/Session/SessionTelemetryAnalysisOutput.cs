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

internal sealed record SessionMemorySpikeThresholdsOutput(
    int MinimumIncreasePercent,
    int MinimumIncreaseMegabytes);

internal sealed record SessionFpsDropDetectionOutput(
    byte ChannelId,
    string ChannelName,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    DateTimeOffset FocusUtc,
    DateTimeOffset BaselineCapturedAtUtc,
    double BaselineFps,
    double MinimumFps,
    double DropFps,
    string Severity,
    double SeverityScore);

internal sealed record SessionMemorySpikeDetectionOutput(
    byte ChannelId,
    string ChannelName,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    DateTimeOffset FocusUtc,
    DateTimeOffset BaselineCapturedAtUtc,
    double BaselineBytes,
    double PeakBytes,
    double DeltaBytes,
    double DeltaPercent,
    DateTimeOffset? RetainedUntilUtc,
    string Severity,
    double SeverityScore);
