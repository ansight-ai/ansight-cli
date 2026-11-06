using Ansight.Host;

namespace Ansight.Cli.Commands.Profiling;

internal sealed record NativeProfilingInspectionOutput(
    string Schema,
    string CaptureId,
    bool IsFound,
    NativeProfileCaptureInspection? Inspection);
