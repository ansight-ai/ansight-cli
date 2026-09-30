namespace Ansight.Host.Runtime.DotNetProfiling;

internal sealed record ParsedTraceAnalysisResult(
    DotNetTraceCaptureManifest Manifest,
    DotNetTraceArtifact NetTraceArtifact,
    TraceSelectionWindow Selection,
    ParsedTraceAnalysis Analysis);
