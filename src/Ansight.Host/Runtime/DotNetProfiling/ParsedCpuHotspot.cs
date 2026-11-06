namespace Ansight.Host.Runtime.DotNetProfiling;

internal sealed record ParsedCpuHotspot(
    string Method,
    string? Module,
    int ExclusiveSamples,
    int InclusiveSamples,
    double ExclusivePercentage,
    double InclusivePercentage);
