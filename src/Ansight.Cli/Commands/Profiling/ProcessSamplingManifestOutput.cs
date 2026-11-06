using Ansight.Host;

namespace Ansight.Cli.Commands.Profiling;

internal sealed record ProcessSamplingManifestOutput(
    string Schema,
    string CaptureId,
    bool IsFound,
    NativeCaptureManifest? Manifest,
    string? ArtifactPath);
