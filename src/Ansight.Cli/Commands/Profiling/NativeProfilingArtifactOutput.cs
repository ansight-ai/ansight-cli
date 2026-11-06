using Ansight.Host;

namespace Ansight.Cli.Commands.Profiling;

internal sealed record NativeProfilingArtifactOutput(
    string Schema,
    string CaptureId,
    bool IsFound,
    NativeProfileArtifactLocation? Artifact);
