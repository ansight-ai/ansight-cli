namespace Ansight.Host.Models.Session;

public sealed record LogEntry
{
    public LogEntry(DateTimeOffset timestampUtc, string message)
    {
        TimestampUtc = timestampUtc;
        Message = message;
    }

    public DateTimeOffset TimestampUtc { get; init; }

    public string Message { get; init; }

    public string StreamId { get; init; } = SessionLogStreamIds.AnsightSdk;

    public LogPriority Priority { get; init; } = LogPriority.Information;

    public string Source { get; init; } = string.Empty;

    public string Tag { get; init; } = string.Empty;

    public string? EventId { get; init; }

    public int? ProcessId { get; init; }

    public int? ThreadId { get; init; }
}
