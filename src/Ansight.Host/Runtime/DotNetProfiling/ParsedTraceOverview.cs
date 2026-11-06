namespace Ansight.Host.Runtime.DotNetProfiling;

internal sealed record ParsedTraceOverview(
    double StartMilliseconds,
    double EndMilliseconds,
    double DurationMilliseconds,
    int EventCount,
    int CpuSampleCount,
    int ProcessCount,
    int ThreadCount,
    int GcCount,
    double TotalGcPauseMilliseconds,
    int JittedMethodCount,
    int ExceptionCount,
    double SymbolicatedSamplePercentage);
