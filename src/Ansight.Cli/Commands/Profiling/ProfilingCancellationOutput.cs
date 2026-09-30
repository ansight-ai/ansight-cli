namespace Ansight.Cli.Commands.Profiling;

internal sealed record ProfilingCancellationOutput(
    string Schema,
    string CaptureId,
    bool IsCancellationRequested);
