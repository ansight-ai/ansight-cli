using Ansight.Host;

namespace Ansight.Cli.Commands.Profiling;

internal sealed record ProfilingAnalysisOutput
{
    public required string Schema { get; init; }

    public required string CaptureId { get; init; }

    public required string AnalyzerVersion { get; init; }

    public required string AnalysisKind { get; init; }

    public required DotNetTraceSelection Selection { get; init; }

    public required DotNetTraceEvidence Evidence { get; init; }

    public ProfilingOverviewOutput? Overview { get; init; }

    public ProfilingStartupTimelineOutput? StartupTimeline { get; init; }

    public ProfilingCpuAnalysisOutput? Cpu { get; init; }

    public DotNetCallTreeNode? CallTree { get; init; }

    public IReadOnlyList<DotNetThreadActivity>? Threads { get; init; }

    public ProfilingGcAnalysisOutput? Gc { get; init; }

    public ProfilingJitAnalysisOutput? Jit { get; init; }

    public ProfilingExceptionAnalysisOutput? Exceptions { get; init; }

    public required IReadOnlyList<string> Warnings { get; init; }
}

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

internal sealed record ProfilingStartupTimelineOutput(
    double DurationMilliseconds,
    IReadOnlyList<ProfilingStartupMilestoneOutput> Milestones,
    bool ApplicationReadyMarkerObserved,
    IReadOnlyList<string> Limitations);

internal sealed record ProfilingStartupMilestoneOutput(
    string Id,
    double TimestampMilliseconds,
    string Description);

internal sealed record ProfilingCpuAnalysisOutput(
    int SampleCount,
    int HotspotCount,
    IReadOnlyList<DotNetCpuHotspot> Hotspots);

internal sealed record ProfilingGcAnalysisOutput(
    int CollectionCount,
    double TotalIntervalMilliseconds,
    double LongestIntervalMilliseconds,
    IReadOnlyDictionary<string, int> GenerationCounts,
    IReadOnlyList<DotNetGcPause> Intervals);

internal sealed record ProfilingJitAnalysisOutput(
    int MethodCount,
    long TotalIlBytes,
    IReadOnlyList<DotNetJitMethod> Methods);

internal sealed record ProfilingExceptionAnalysisOutput(
    int ThrowCount,
    int GroupCount,
    IReadOnlyList<DotNetExceptionGroup> Groups);
