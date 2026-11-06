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
