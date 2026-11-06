namespace Ansight.Host.Profiling;

public sealed record PerfettoCaptureInspection(
    string? ConfigPath,
    int? ConfiguredDurationMilliseconds,
    IReadOnlyList<string> DataSources,
    NativeProfileInspectionToolchain Toolchain,
    PerfettoQueryInspection? TraceOverview,
    IReadOnlyList<string> Warnings);
