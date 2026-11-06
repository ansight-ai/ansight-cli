namespace Ansight.Host.Sessions.Capture;

internal sealed class SessionVisualTreeSnapshotIndexEntry
{
    public required string EntryPath { get; init; }
    public required SessionVisualTreeSnapshotHeader Header { get; init; }
}
