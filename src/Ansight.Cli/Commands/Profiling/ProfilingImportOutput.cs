using Ansight.Host;

namespace Ansight.Cli.Commands.Profiling;

internal sealed record ProfilingImportOutput(
    string Schema,
    DotNetCaptureManifest Capture);
