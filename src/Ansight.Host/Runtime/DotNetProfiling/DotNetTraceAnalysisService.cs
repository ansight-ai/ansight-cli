using System.Collections.Concurrent;

namespace Ansight.Host.Runtime.DotNetProfiling;

internal sealed class DotNetTraceAnalysisService
{
    public const string AnalyzerVersion = "dotnet-traceevent-v2";
    private readonly DotNetTraceCaptureStore captureStore;
    private readonly DotNetTraceAnalyzer analyzer;
    private readonly ConcurrentDictionary<AnalysisCacheKey, Lazy<ParsedTraceAnalysis>> cache = new();

    public DotNetTraceAnalysisService(
        DotNetTraceCaptureStore captureStore,
        DotNetTraceAnalyzer? analyzer = null)
    {
        this.captureStore = captureStore;
        this.analyzer = analyzer ?? new DotNetTraceAnalyzer();
    }

    public ParsedTraceAnalysisResult Analyze(
        string captureId,
        TraceSelectionWindow? selection = null,
        int hotspotLimit = 100,
        int callTreeDepth = 64,
        int callTreeChildLimit = 100)
    {
        if (!captureStore.TryLoadManifest(captureId, out var manifest) || manifest is null)
        {
            throw new KeyNotFoundException($".NET trace capture '{captureId}' was not found.");
        }

        if (!captureStore.TryResolveArtifactPath(
                manifest,
                DotNetProfilingEngine.NetTraceArtifactKind,
                out var tracePath)
            || tracePath is null)
        {
            throw new InvalidOperationException(
                $"Capture '{captureId}' does not contain a readable .nettrace artifact.");
        }

        var artifact = manifest.Artifacts.First(item =>
            string.Equals(item.Kind, DotNetProfilingEngine.NetTraceArtifactKind, StringComparison.Ordinal));
        selection ??= TraceSelectionWindow.EntireTrace;
        var cacheKey = new AnalysisCacheKey(
            captureId,
            artifact.Sha256,
            selection,
            Math.Clamp(hotspotLimit, 1, 1000),
            Math.Clamp(callTreeDepth, 1, 256),
            Math.Clamp(callTreeChildLimit, 1, 1000));
        var analysis = cache.GetOrAdd(
                cacheKey,
                key => new Lazy<ParsedTraceAnalysis>(
                    () => analyzer.Analyze(
                        tracePath,
                        key.Selection,
                        key.HotspotLimit,
                        key.CallTreeDepth,
                        key.CallTreeChildLimit),
                    LazyThreadSafetyMode.ExecutionAndPublication))
            .Value;
        return new ParsedTraceAnalysisResult(manifest, artifact, selection, analysis);
    }

    private sealed record AnalysisCacheKey(
        string CaptureId,
        string TraceHash,
        TraceSelectionWindow Selection,
        int HotspotLimit,
        int CallTreeDepth,
        int CallTreeChildLimit);
}
