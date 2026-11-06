using Ansight.Host;

namespace Ansight.Cli.Commands.Profiling;

internal sealed record ProcessSamplingCaptureOutput(
    string Schema,
    string Operation,
    string CaptureId,
    bool IsFound,
    NativeCaptureSnapshot? Capture,
    string? ArtifactPath);
