namespace Ansight.Host.Runtime.NativeProfiling;

internal static class NativeProfilePlatforms
{
    public const string Ios = "ios";
    public const string Android = "android";
    public const string Process = "process";
}

internal static class NativeProfilePresets
{
    public const string Launch = "launch";
    public const string Cpu = "cpu";
    public const string System = "system";
    public const string Memory = "memory";
    public const string Leaks = "leaks";
    public const string NativeHeap = "native-heap";
    public const string ManagedHeap = "managed-heap";
    public const string StackSample = "stack-sample";
}

internal enum NativeProfileCaptureState
{
    Queued,
    InspectingArtifact,
    InspectingProcess,
    PreparingDevice,
    Capturing,
    DerivingArtifacts,
    Completed,
    Failed,
    Cancelled
}

internal sealed record NativeProfileCaptureRequest(
    string Platform,
    string ApplicationPath,
    string AppId,
    string DeviceId,
    string Preset,
    TimeSpan Duration,
    bool Headless = false);

internal sealed record ProcessSampleCaptureRequest(
    int ProcessId,
    TimeSpan Duration,
    int IntervalMilliseconds);

internal sealed record NativeProfileArtifact(
    string Kind,
    string RelativePath,
    long Length,
    string Sha256,
    bool IsDirectory,
    bool Authoritative);

internal sealed record NativeProfileCaptureManifest
{
    public const string CurrentSchema = "ansight.native-profile-capture/v1";
    public const string ProcessSampleSchema = "ansight.process-sample-capture/v1";

    public required string Schema { get; init; }

    public required string CaptureId { get; init; }

    public required string Platform { get; init; }

    public required string Engine { get; init; }

    public required string Preset { get; init; }

    public required string AppId { get; init; }

    public required string ApplicationPath { get; init; }

    public required string DeviceId { get; init; }

    public required int RequestedDurationSeconds { get; init; }

    public required NativeProfileCaptureState State { get; init; }

    public required DateTimeOffset CreatedUtc { get; init; }

    public DateTimeOffset? StartedUtc { get; init; }

    public DateTimeOffset? CompletedUtc { get; init; }

    public string? StopReason { get; init; }

    public string? FailureMessage { get; init; }

    public string? CaptureToolPath { get; init; }

    public string? CaptureToolVersion { get; init; }

    public string? SupportingToolVersion { get; init; }

    public int? ProcessId { get; init; }

    public string? ProcessName { get; init; }

    public int? SampleIntervalMilliseconds { get; init; }

    public IReadOnlyList<NativeProfileArtifact> Artifacts { get; init; } = [];

    public IReadOnlyList<string> Warnings { get; init; } = [];
}

internal sealed record NativeProfileCaptureSnapshot(
    string CaptureId,
    NativeProfileCaptureState State,
    string Phase,
    DateTimeOffset CreatedUtc,
    DateTimeOffset? StartedUtc,
    DateTimeOffset? CompletedUtc,
    string? FailureMessage,
    NativeProfileCaptureManifest Manifest,
    IReadOnlyList<string> RecentLogLines)
{
    public bool IsTerminal => State is NativeProfileCaptureState.Completed
        or NativeProfileCaptureState.Failed
        or NativeProfileCaptureState.Cancelled;
}

internal sealed record NativeProfileToolchain(
    string Platform,
    string Engine,
    bool IsAvailable,
    string? CaptureToolPath,
    string? CaptureToolVersion,
    string? SupportingToolVersion,
    string Message);

internal sealed record NativeProfileProducedArtifact(
    string Kind,
    string Path,
    bool IsDirectory,
    bool Authoritative);

internal sealed record NativeProfileAdapterCaptureResult(
    IReadOnlyList<NativeProfileProducedArtifact> Artifacts,
    IReadOnlyList<string> Warnings);
