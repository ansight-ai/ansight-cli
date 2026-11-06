namespace Ansight.Host.Profiling;

public sealed record ProcessSampleRequest(
    int ProcessId,
    TimeSpan Duration,
    int IntervalMilliseconds);
