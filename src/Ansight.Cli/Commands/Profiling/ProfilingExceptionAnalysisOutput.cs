using Ansight.Host;

namespace Ansight.Cli.Commands.Profiling;

internal sealed record ProfilingExceptionAnalysisOutput(
    int ThrowCount,
    int GroupCount,
    IReadOnlyList<DotNetExceptionGroup> Groups);
