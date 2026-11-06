namespace Ansight.Host.Runtime.DotNetProfiling;

internal sealed record ParsedGcPause(
    int Number,
    int Generation,
    string Reason,
    string Type,
    double StartMilliseconds,
    double EndMilliseconds,
    double DurationMilliseconds,
    int ProcessId,
    int ThreadId);
