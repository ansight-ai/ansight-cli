namespace Ansight.Host.Models.Session;



public sealed class SessionLogStream
{
    public required string StreamId { get; init; }
    public required string Kind { get; init; }
    public required string DisplayName { get; init; }
    public string Status { get; init; } = SessionLogStreamStatuses.Completed;
    public DateTimeOffset? StartedUtc { get; init; }
    public DateTimeOffset? EndedUtc { get; init; }
    public string? StatusMessage { get; init; }
    public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);
    public IReadOnlyList<LogEntry> Entries { get; init; } = Array.Empty<LogEntry>();
    public int TotalEntryCount { get; init; }
    public int RetainedEntryStartIndex { get; init; }
}
