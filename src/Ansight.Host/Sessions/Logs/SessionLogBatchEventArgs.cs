namespace Ansight.Host.Models.Session;

public sealed class SessionLogBatchEventArgs : EventArgs
{
    public SessionLogBatchEventArgs(
        string sessionId,
        string appId,
        string streamId,
        IReadOnlyList<LogEntry> entries,
        int streamEntryCount,
        int totalEntryCount)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        ArgumentException.ThrowIfNullOrWhiteSpace(streamId);
        ArgumentNullException.ThrowIfNull(entries);

        SessionId = sessionId;
        AppId = appId;
        StreamId = streamId;
        Entries = entries;
        StreamEntryCount = streamEntryCount;
        TotalEntryCount = totalEntryCount;
    }

    public string SessionId { get; }

    public string AppId { get; }

    public string StreamId { get; }

    public IReadOnlyList<LogEntry> Entries { get; }

    public int StreamEntryCount { get; }

    public int TotalEntryCount { get; }
}
