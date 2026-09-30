using Ansight.Host;

namespace Ansight.Cli.Commands.Profiling;

internal sealed record ProfilingCaptureListOutput(
    string Schema,
    IReadOnlyList<DotNetCaptureManifest> Captures);
