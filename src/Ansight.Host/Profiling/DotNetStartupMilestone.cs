namespace Ansight.Host.Profiling;

public sealed record DotNetStartupMilestone(
    string Kind,
    string Provider,
    string EventName,
    double TimestampMilliseconds,
    int ProcessId,
    int ThreadId);
