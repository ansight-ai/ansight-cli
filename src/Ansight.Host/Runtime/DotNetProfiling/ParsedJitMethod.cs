namespace Ansight.Host.Runtime.DotNetProfiling;

internal sealed record ParsedJitMethod(
    string Method,
    int IlSize,
    double TimestampMilliseconds,
    int ProcessId,
    int ThreadId);
