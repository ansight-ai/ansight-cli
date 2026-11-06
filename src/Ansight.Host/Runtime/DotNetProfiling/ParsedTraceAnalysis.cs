namespace Ansight.Host.Runtime.DotNetProfiling;

internal sealed record ParsedTraceAnalysis(
    ParsedTraceOverview Overview,
    IReadOnlyList<ParsedCpuHotspot> CpuHotspots,
    ParsedCallTreeNode CallTree,
    IReadOnlyList<ParsedGcPause> GcPauses,
    IReadOnlyList<ParsedJitMethod> JittedMethods,
    IReadOnlyList<ParsedExceptionGroup> Exceptions,
    IReadOnlyList<ParsedThreadActivity> Threads,
    IReadOnlyList<ParsedStartupMilestone> StartupMilestones,
    IReadOnlyDictionary<string, int> ProviderEventCounts,
    IReadOnlyDictionary<string, int> EventNameCounts,
    IReadOnlyList<string> Warnings);
