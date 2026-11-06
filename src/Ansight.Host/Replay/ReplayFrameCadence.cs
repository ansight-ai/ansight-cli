namespace Ansight.Host.Replay;

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
