namespace Ansight.Host.Runtime.NativeProfiling;

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
