namespace Ansight.Host.Runtime.Diagnostics;

public sealed record RuntimeLogEntry(
    DateTimeOffset TimestampUtc,
    string Message,
    string Source,
    string? Tag,
    string? SessionId);
