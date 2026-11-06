namespace Ansight.Host.Profiling;

public sealed record DotNetGcPause(
    int Number,
    int Generation,
    string Reason,
    string Type,
    double StartMilliseconds,
    double EndMilliseconds,
    double DurationMilliseconds,
    int ProcessId,
    int ThreadId);
