namespace Ansight.RemoteSimulator.Core.Recording;

public sealed record RemoteRecordingLogStreamSummary(
    string Id,
    string Kind,
    string DisplayName,
    string Status,
    int EntryCount);
