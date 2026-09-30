namespace Ansight.Host.Profiling;

public sealed record NativeCaptureRequest(
    string Platform,
    string ApplicationPath,
    string AppId,
    string DeviceId,
    string Preset,
    TimeSpan Duration,
    bool Headless = false);

public sealed record ProcessSampleRequest(
    int ProcessId,
    TimeSpan Duration,
    int IntervalMilliseconds);

public sealed record NativeCaptureArtifact(
    string Kind,
    string RelativePath,
    long Length,
    string Sha256,
    bool IsDirectory,
    bool Authoritative);

public sealed record NativeCaptureManifest(
    string Schema,
    string CaptureId,
    string Platform,
    string Engine,
    string Preset,
    string AppId,
    string ApplicationPath,
    string DeviceId,
    int RequestedDurationSeconds,
    string State,
    DateTimeOffset CreatedUtc,
    DateTimeOffset? StartedUtc,
    DateTimeOffset? CompletedUtc,
    string? StopReason,
    string? FailureMessage,
    string? CaptureToolPath,
    string? CaptureToolVersion,
    string? SupportingToolVersion,
    IReadOnlyList<NativeCaptureArtifact> Artifacts,
    IReadOnlyList<string> Warnings,
    int? ProcessId,
    string? ProcessName,
    int? SampleIntervalMilliseconds);

public sealed record NativeCaptureSnapshot(
    string CaptureId,
    string State,
    string Phase,
    DateTimeOffset CreatedUtc,
    DateTimeOffset? StartedUtc,
    DateTimeOffset? CompletedUtc,
    string? FailureMessage,
    NativeCaptureManifest Manifest,
    IReadOnlyList<string> RecentLogLines)
{
    public bool IsTerminal => State is "completed" or "failed" or "cancelled";
}

public sealed record NativeProfilingToolchain(
    string Platform,
    string Engine,
    bool IsAvailable,
    string? CaptureToolPath,
    string? CaptureToolVersion,
    string? SupportingToolVersion,
    string Message);

public sealed record NativeProfileArtifactLocation(
    string CaptureId,
    string Platform,
    string Path,
    NativeCaptureArtifact Artifact);

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

public sealed record InstrumentsTocInspection(
    string Path,
    string RootElement,
    int RunCount,
    int TableCount,
    IReadOnlyList<InstrumentsSchemaInspection> Schemas,
    IReadOnlyList<InstrumentsProcessInspection> Processes);

public sealed record InstrumentsSchemaInspection(string Schema, int Occurrences);

public sealed record InstrumentsProcessInspection(string? ProcessId, string? Name, string? Path);

public sealed record NativeProfileInspectionToolchain(
    bool IsAvailable,
    string? ToolPath,
    string? Version,
    string Message);

public sealed record PerfettoCaptureInspection(
    string? ConfigPath,
    int? ConfiguredDurationMilliseconds,
    IReadOnlyList<string> DataSources,
    NativeProfileInspectionToolchain Toolchain,
    PerfettoQueryInspection? TraceOverview,
    IReadOnlyList<string> Warnings);

public sealed record PerfettoQueryInspection(
    string CaptureId,
    NativeProfileInspectionToolchain Toolchain,
    string Sql,
    IReadOnlyList<string> Columns,
    IReadOnlyList<IReadOnlyList<string?>> Rows,
    bool WasTruncated,
    int RowCount);
