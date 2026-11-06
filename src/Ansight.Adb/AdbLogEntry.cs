namespace Ansight.Adb;

public sealed record AdbLogEntry(
    DateTimeOffset TimestampUtc,
    int ProcessId,
    int ThreadId,
    AdbLogPriority Priority,
    string Tag,
    string Message);
