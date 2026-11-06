namespace Ansight.Host.Runtime.NativeProfiling;

internal sealed record ProcessSampleAdapterContext(
    string CapturePath,
    int ProcessId,
    TimeSpan Duration,
    int IntervalMilliseconds,
    Action<string> Log);
