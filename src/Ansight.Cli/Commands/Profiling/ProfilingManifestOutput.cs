using Ansight.Host;

namespace Ansight.Cli.Commands.Profiling;

internal sealed record ProfilingManifestOutput(
    string Schema,
    string CaptureId,
    bool IsFound,
    DotNetCaptureManifest? Manifest);
