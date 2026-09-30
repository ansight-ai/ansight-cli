using Ansight.Host;

namespace Ansight.Cli.Commands.Profiling;

internal sealed record ProcessSamplingCaptureOutput(
    string Schema,
    string Operation,
    string CaptureId,
    bool IsFound,
    NativeCaptureSnapshot? Capture,
    string? ArtifactPath);

internal sealed record ProcessSamplingCaptureListOutput(
    string Schema,
    IReadOnlyList<NativeCaptureManifest> Captures);

internal sealed record ProcessSamplingManifestOutput(
    string Schema,
    string CaptureId,
    bool IsFound,
    NativeCaptureManifest? Manifest,
    string? ArtifactPath);

internal sealed record ProcessSamplingArtifactOutput(
    string Schema,
    string CaptureId,
    bool IsFound,
    string? ArtifactPath);
