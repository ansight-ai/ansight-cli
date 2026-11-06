using Ansight.Host;

namespace Ansight.Cli.Commands.Profiling;

internal sealed record NativeProfilingCaptureOutput(
    string Schema,
    string Operation,
    string CaptureId,
    bool IsFound,
    NativeCaptureSnapshot? Capture);
