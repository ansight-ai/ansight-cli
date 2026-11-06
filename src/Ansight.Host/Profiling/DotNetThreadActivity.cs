namespace Ansight.Host.Profiling;

public sealed record DotNetThreadActivity(
    int ProcessId,
    int ThreadId,
    string? ProcessName,
    string? ThreadName,
    int CpuSampleCount,
    double CpuSamplePercentage);
