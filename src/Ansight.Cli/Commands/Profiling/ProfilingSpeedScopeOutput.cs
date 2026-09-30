using Ansight.Host;

namespace Ansight.Cli.Commands.Profiling;

internal sealed record ProfilingSpeedScopeOutput(
    string Schema,
    string CaptureId,
    bool IsFound,
    DotNetCaptureArtifactLocation? SpeedScope);
