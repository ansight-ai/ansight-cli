namespace Ansight.Host.Profiling;

public sealed record DotNetTraceAnalysisResult(
    string CaptureId,
    string AnalyzerVersion,
    DotNetTraceSelection Selection,
    DotNetTraceEvidence Evidence,
    DotNetTraceAnalysis Analysis);
