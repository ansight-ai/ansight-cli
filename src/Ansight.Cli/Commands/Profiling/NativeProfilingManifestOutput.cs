using Ansight.Host;

namespace Ansight.Cli.Commands.Profiling;

internal sealed record NativeProfilingManifestOutput(
    string Schema,
    string CaptureId,
    bool IsFound,
    NativeCaptureManifest? Manifest);
