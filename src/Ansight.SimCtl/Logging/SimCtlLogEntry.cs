namespace Ansight.SimCtl;

public sealed record SimCtlLogEntry(
    DateTimeOffset TimestampUtc,
    int ProcessId,
    long ThreadId,
    SimCtlLogPriority Priority,
    string Subsystem,
    string Category,
    string ProcessImagePath,
    string Message);
