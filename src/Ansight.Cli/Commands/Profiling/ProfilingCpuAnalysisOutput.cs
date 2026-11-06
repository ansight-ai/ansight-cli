using Ansight.Host;

namespace Ansight.Cli.Commands.Profiling;

internal sealed record ProfilingCpuAnalysisOutput(
    int SampleCount,
    int HotspotCount,
    IReadOnlyList<DotNetCpuHotspot> Hotspots);
