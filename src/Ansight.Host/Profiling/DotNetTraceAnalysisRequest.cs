namespace Ansight.Host.Profiling;

public sealed record DotNetTraceAnalysisRequest(
    string CaptureId,
    DotNetTraceSelection? Selection = null,
    int HotspotLimit = 100,
    int CallTreeDepth = 64,
    int CallTreeChildLimit = 100);
