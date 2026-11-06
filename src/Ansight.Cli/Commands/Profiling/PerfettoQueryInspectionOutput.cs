using Ansight.Host;

namespace Ansight.Cli.Commands.Profiling;

internal sealed record PerfettoQueryInspectionOutput(
    string Schema,
    string CaptureId,
    PerfettoQueryInspection Query);
