namespace Ansight.Host.Runtime.NativeProfiling;

internal sealed record ProcessSampleCaptureRequest(
    int ProcessId,
    TimeSpan Duration,
    int IntervalMilliseconds);
