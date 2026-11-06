namespace Ansight.Host.Profiling;

public sealed record DotNetJitMethod(
    string Method,
    int IlSize,
    double TimestampMilliseconds,
    int ProcessId,
    int ThreadId);
