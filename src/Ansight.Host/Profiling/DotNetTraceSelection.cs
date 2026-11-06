namespace Ansight.Host.Profiling;

public sealed record DotNetTraceSelection(
    double? StartMilliseconds,
    double? EndMilliseconds,
    int? ProcessId,
    int? ThreadId);
