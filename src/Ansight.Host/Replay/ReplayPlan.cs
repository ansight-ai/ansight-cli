namespace Ansight.Host.Replay;

/// <summary>
/// A bounded set of semantic instructions derived from recorded interaction evidence.
/// </summary>
public sealed record ReplayPlan(
    string Schema,
    string SourceKind,
    string SourceId,
    string? AppId,
    int SourceEventCount,
    IReadOnlyList<ReplayStep> Steps,
    IReadOnlyList<string> Diagnostics)
{
    public ReplayFrameCadence? FrameCadence { get; init; }

    public IReadOnlyList<string> Instructions => Steps
        .Select(static step => step.Instruction)
        .ToArray();
}

/// <summary>
/// Describes whether screenshot timing is dense enough to support reliable
/// extraction of the selected replay range.
/// </summary>
public sealed record ReplayFrameCadence(
    string Rating,
    bool AllowsLiveReplay,
    string Message,
    int FrameCount,
    double SelectedRangeDurationSeconds,
    double EffectiveFramesPerSecond,
    double? MedianIntervalMilliseconds,
    double? P95IntervalMilliseconds,
    double MaximumGapMilliseconds,
    int TimedReplayStepCount,
    int CoveredReplayStepCount,
    double? ReplayStepCoveragePercent,
    double CoverageWindowMilliseconds,
    int DistinctSupportingFrameCount,
    double? DistinctSupportingFramePercent,
    double? MedianReplayStepFrameDistanceMilliseconds,
    double? P95ReplayStepFrameDistanceMilliseconds,
    double? MaximumReplayStepFrameDistanceMilliseconds,
    IReadOnlyList<string> Findings);

public sealed record ReplayStep(
    int Sequence,
    string Kind,
    string Instruction,
    DateTimeOffset? CapturedAtUtc = null,
    string? Target = null)
{
    public ReplaySourceEvidence? SourceEvidence { get; init; }
}

/// <summary>
/// Identifies the captured evidence used to derive a replay step without making
/// capture-local node identifiers part of the live action contract.
/// </summary>
public sealed record ReplaySourceEvidence
{
    public string? VisualTreeSnapshotId { get; init; }

    public string? ScreenshotFrameId { get; init; }

    public double? StartNormalizedX { get; init; }

    public double? StartNormalizedY { get; init; }

    public double? EndNormalizedX { get; init; }

    public double? EndNormalizedY { get; init; }

    public long? DurationMilliseconds { get; init; }
}
