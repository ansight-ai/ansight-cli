using Ansight.Host;

namespace Ansight.Cli.Commands.Profiling;

internal sealed record ProcessSamplingArtifactOutput(
    string Schema,
    string CaptureId,
    bool IsFound,
    string? ArtifactPath);
