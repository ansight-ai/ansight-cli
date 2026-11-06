namespace Ansight.Host.Profiling;

public sealed record DotNetCpuHotspot(
    string Method,
    string? Module,
    int ExclusiveSamples,
    int InclusiveSamples,
    double ExclusivePercentage,
    double InclusivePercentage);
