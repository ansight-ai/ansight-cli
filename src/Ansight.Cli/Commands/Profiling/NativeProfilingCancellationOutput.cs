using Ansight.Host;

namespace Ansight.Cli.Commands.Profiling;

internal sealed record NativeProfilingCancellationOutput(
    string Schema,
    string CaptureId,
    bool CancellationRequested);
