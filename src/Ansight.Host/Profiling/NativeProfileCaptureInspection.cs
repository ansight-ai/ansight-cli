namespace Ansight.Host.Profiling;

public sealed record NativeProfileCaptureInspection(
    string AnalyzerVersion,
    string CaptureId,
    string Platform,
    string Engine,
    string Preset,
    string State,
    int RequestedDurationSeconds,
    double? CaptureElapsedSeconds,
    string CaptureDirectoryPath,
    NativeProfileArtifactLocation? AuthoritativeArtifact,
    IReadOnlyList<NativeProfileArtifactLocation> Artifacts,
    InstrumentsTocInspection? Instruments,
    PerfettoCaptureInspection? Perfetto,
    IReadOnlyList<string> Warnings);
