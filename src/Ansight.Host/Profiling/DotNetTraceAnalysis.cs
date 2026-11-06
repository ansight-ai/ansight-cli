namespace Ansight.Host.Profiling;

public sealed record DotNetTraceAnalysis(
    DotNetTraceOverview Overview,
    IReadOnlyList<DotNetCpuHotspot> CpuHotspots,
    DotNetCallTreeNode CallTree,
    IReadOnlyList<DotNetGcPause> GcPauses,
    IReadOnlyList<DotNetJitMethod> JittedMethods,
    IReadOnlyList<DotNetExceptionGroup> Exceptions,
    IReadOnlyList<DotNetThreadActivity> Threads,
    IReadOnlyList<DotNetStartupMilestone> StartupMilestones,
    IReadOnlyDictionary<string, int> ProviderEventCounts,
    IReadOnlyDictionary<string, int> EventNameCounts,
    IReadOnlyList<string> Warnings);
