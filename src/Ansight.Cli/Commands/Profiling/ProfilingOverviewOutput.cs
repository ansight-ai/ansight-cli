using Ansight.Host;

namespace Ansight.Cli.Commands.Profiling;

internal sealed record ProfilingOverviewOutput(
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
    double SymbolicatedSamplePercentage,
    IReadOnlyDictionary<string, int> ProviderEventCounts,
    IReadOnlyDictionary<string, int> EventNameCounts);
