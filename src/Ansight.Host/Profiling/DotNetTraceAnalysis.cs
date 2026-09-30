namespace Ansight.Host.Profiling;

public sealed record DotNetTraceSelection(
    double? StartMilliseconds,
    double? EndMilliseconds,
    int? ProcessId,
    int? ThreadId);

public sealed record DotNetTraceAnalysisRequest(
    string CaptureId,
    DotNetTraceSelection? Selection = null,
    int HotspotLimit = 100,
    int CallTreeDepth = 64,
    int CallTreeChildLimit = 100);

public sealed record DotNetTraceEvidence(
    string ArtifactKind,
    string RelativePath,
    long Length,
    string Sha256);

public sealed record DotNetTraceAnalysisResult(
    string CaptureId,
    string AnalyzerVersion,
    DotNetTraceSelection Selection,
    DotNetTraceEvidence Evidence,
    DotNetTraceAnalysis Analysis);

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

public sealed record DotNetTraceOverview(
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

public sealed record DotNetCpuHotspot(
    string Method,
    string? Module,
    int ExclusiveSamples,
    int InclusiveSamples,
    double ExclusivePercentage,
    double InclusivePercentage);

public sealed record DotNetCallTreeNode(
    string Name,
    string? Module,
    int SampleCount,
    IReadOnlyList<DotNetCallTreeNode> Children);

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

public sealed record DotNetJitMethod(
    string Method,
    int IlSize,
    double TimestampMilliseconds,
    int ProcessId,
    int ThreadId);

public sealed record DotNetExceptionGroup(
    string Type,
    string? Message,
    int Count,
    double FirstTimestampMilliseconds,
    double LastTimestampMilliseconds);

public sealed record DotNetThreadActivity(
    int ProcessId,
    int ThreadId,
    string? ProcessName,
    string? ThreadName,
    int CpuSampleCount,
    double CpuSamplePercentage);

public sealed record DotNetStartupMilestone(
    string Kind,
    string Provider,
    string EventName,
    double TimestampMilliseconds,
    int ProcessId,
    int ThreadId);
