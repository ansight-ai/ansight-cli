using Ansight.Host;

namespace Ansight.Cli.Commands.Profiling;

internal sealed record ProcessSamplingCaptureListOutput(
    string Schema,
    IReadOnlyList<NativeCaptureManifest> Captures);
