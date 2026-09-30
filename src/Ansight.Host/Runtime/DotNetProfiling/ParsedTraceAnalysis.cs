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

internal sealed record ParsedCpuHotspot(
    string Method,
    string? Module,
    int ExclusiveSamples,
    int InclusiveSamples,
    double ExclusivePercentage,
    double InclusivePercentage);

internal sealed record ParsedCallTreeNode(
    string Name,
    string? Module,
    int SampleCount,
    IReadOnlyList<ParsedCallTreeNode> Children);

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

internal sealed record ParsedJitMethod(
    string Method,
    int IlSize,
    double TimestampMilliseconds,
    int ProcessId,
    int ThreadId);

internal sealed record ParsedExceptionGroup(
    string Type,
    string? Message,
    int Count,
    double FirstTimestampMilliseconds,
    double LastTimestampMilliseconds);

internal sealed record ParsedThreadActivity(
    int ProcessId,
    int ThreadId,
    string? ProcessName,
    string? ThreadName,
    int CpuSampleCount,
    double CpuSamplePercentage);

internal sealed record ParsedStartupMilestone(
    string Provider,
    string EventName,
    double TimestampMilliseconds,
    int ProcessId,
    int ThreadId);
