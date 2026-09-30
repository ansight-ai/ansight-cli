namespace Ansight.Host.Runtime.SessionCaptureStorage;



internal sealed class SessionLogPersistenceBatch
{
    public SessionLogPersistenceBatch(SessionLogBatchEventArgs batch)
    {
        SessionId = batch.SessionId;
        AppId = batch.AppId;
        StreamId = batch.StreamId;
        Entries.AddRange(batch.Entries);
        StartEntryIndex = Math.Max(0, batch.StreamEntryCount - batch.Entries.Count);
        StreamEntryCount = batch.StreamEntryCount;
        TotalEntryCount = batch.TotalEntryCount;
    }

    public string SessionId { get; }

    public string AppId { get; }

    public string StreamId { get; }

    public List<LogEntry> Entries { get; } = [];

    public int StartEntryIndex { get; }

    public int StreamEntryCount { get; private set; }

    public int TotalEntryCount { get; private set; }

    public void Append(SessionLogBatchEventArgs batch)
    {
        Entries.AddRange(batch.Entries);
        StreamEntryCount = Math.Max(StreamEntryCount, batch.StreamEntryCount);
        TotalEntryCount = Math.Max(TotalEntryCount, batch.TotalEntryCount);
    }
}
