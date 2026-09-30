namespace Ansight.Host.Runtime.DotNetProfiling;

internal sealed record DotNetTraceCaptureManifest
{
    public const string CurrentSchema = "ansight.dotnet-trace-capture/v2";

    public required string Schema { get; init; }

    public required string CaptureId { get; init; }

    public required string AppId { get; init; }

    public required string ApplicationPath { get; init; }

    public string? Platform { get; init; }

    public string? ArtifactKind { get; init; }

    public string? LaunchAdapter { get; init; }

    public string? DeviceId { get; init; }

    public required string CapturePreset { get; init; }

    public required int RequestedDurationSeconds { get; init; }

    public required DotNetTraceCaptureState State { get; init; }

    public required DateTimeOffset CreatedUtc { get; init; }

    public DateTimeOffset? StartedUtc { get; init; }

    public DateTimeOffset? CompletedUtc { get; init; }

    public string? StopReason { get; init; }

    public string? FailureMessage { get; init; }

    public string? DotNetTraceVersion { get; init; }

    public string? DotNetDsRouterVersion { get; init; }

    public string? AdbVersion { get; init; }

    public string? XcodeVersion { get; init; }

    public IReadOnlyList<DotNetTraceArtifact> Artifacts { get; init; } = [];

    public IReadOnlyList<string> Warnings { get; init; } = [];
}
