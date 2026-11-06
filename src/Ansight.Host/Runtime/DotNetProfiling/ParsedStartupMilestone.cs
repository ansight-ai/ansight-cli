namespace Ansight.Host.Runtime.DotNetProfiling;

internal sealed record ParsedStartupMilestone(
    string Provider,
    string EventName,
    double TimestampMilliseconds,
    int ProcessId,
    int ThreadId);
