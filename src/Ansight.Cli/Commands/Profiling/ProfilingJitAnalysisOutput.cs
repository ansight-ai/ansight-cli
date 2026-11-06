using Ansight.Host;

namespace Ansight.Cli.Commands.Profiling;

internal sealed record ProfilingJitAnalysisOutput(
    int MethodCount,
    long TotalIlBytes,
    IReadOnlyList<DotNetJitMethod> Methods);
