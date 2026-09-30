using Ansight.Host;

namespace Ansight.Cli.Commands.Profiling;

internal sealed record ProfilingCaptureOutput(
    string Schema,
    string Operation,
    string CaptureId,
    bool IsFound,
    DotNetCaptureSnapshot? Capture);
