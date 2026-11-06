using Ansight.Host;

namespace Ansight.Cli.Commands.Profiling;

internal sealed record ProfilingGcAnalysisOutput(
    int CollectionCount,
    double TotalIntervalMilliseconds,
    double LongestIntervalMilliseconds,
    IReadOnlyDictionary<string, int> GenerationCounts,
    IReadOnlyList<DotNetGcPause> Intervals);
