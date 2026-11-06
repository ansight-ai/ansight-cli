using Ansight.Host;

namespace Ansight.Cli.Commands.Profiling;

internal sealed record NativeProfilingCaptureListOutput(
    string Schema,
    string Platform,
    IReadOnlyList<NativeCaptureManifest> Captures);
