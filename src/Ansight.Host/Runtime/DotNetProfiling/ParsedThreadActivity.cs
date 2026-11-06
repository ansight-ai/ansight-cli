namespace Ansight.Host.Runtime.DotNetProfiling;

internal sealed record ParsedThreadActivity(
    int ProcessId,
    int ThreadId,
    string? ProcessName,
    string? ThreadName,
    int CpuSampleCount,
    double CpuSamplePercentage);
