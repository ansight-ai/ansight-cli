namespace Ansight.Host.Runtime.Archives;

internal static class SessionArchiveLogSelection
{
    public static IReadOnlyList<SessionLogStream> Select(
        AppSessionSnapshot snapshot,
        bool includeNativeDeviceLogs)
    {
        var streams = SessionLogStreams.Normalize(snapshot.LogStreams, snapshot.Logs);
        return includeNativeDeviceLogs
            ? streams
            : streams.Where(stream => !IsNativeDeviceLog(stream)).ToArray();
    }

    public static int CountTotalEntries(
        AppSessionSnapshot snapshot,
        IReadOnlyList<SessionLogStream> selectedStreams)
    {
        var allStreams = SessionLogStreams.Normalize(snapshot.LogStreams, snapshot.Logs);
        if (selectedStreams.Count == allStreams.Count)
        {
            return Math.Max(snapshot.TotalLogCount, snapshot.Logs.Count);
        }

        long count = 0;
        foreach (var stream in selectedStreams)
        {
            count += Math.Max(stream.TotalEntryCount, stream.Entries.Count);
        }

        return (int)Math.Min(count, int.MaxValue);
    }

    private static bool IsNativeDeviceLog(SessionLogStream stream)
        => string.Equals(stream.StreamId, SessionLogStreamIds.AndroidLogcat, StringComparison.Ordinal)
           || string.Equals(stream.StreamId, SessionLogStreamIds.AppleUnifiedLog, StringComparison.Ordinal)
           || string.Equals(stream.Kind, SessionLogStreamKinds.AndroidLogcat, StringComparison.Ordinal)
           || string.Equals(stream.Kind, SessionLogStreamKinds.AppleUnifiedLog, StringComparison.Ordinal);
}
