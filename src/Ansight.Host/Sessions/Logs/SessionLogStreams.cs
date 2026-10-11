namespace Ansight.Host.Models.Session;



public static class SessionLogStreams
{
    public static IReadOnlyList<SessionLogStream> Normalize(
        IReadOnlyList<SessionLogStream>? streams,
        IReadOnlyList<LogEntry>? legacyLogs)
    {
        var normalizedStreams = streams is { Count: > 0 }
            ? streams
                .Where(stream => stream is not null && !string.IsNullOrWhiteSpace(stream.StreamId))
                .Select(CloneNormalized)
                .ToArray()
            : Array.Empty<SessionLogStream>();
        if (legacyLogs is not { Count: > 0 })
        {
            return normalizedStreams;
        }

        if (HaveMatchingEntryCounts(normalizedStreams, legacyLogs))
        {
            return normalizedStreams;
        }

        var entriesByStreamId = legacyLogs
            .GroupBy(entry => NormalizeStreamId(entry.StreamId), StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<LogEntry>)group
                    .Select(entry => WithStreamId(entry, group.Key))
                    .ToArray(),
                StringComparer.Ordinal);
        if (normalizedStreams.Length == 0)
        {
            return entriesByStreamId
                .Select(pair => CreateStreamForEntries(pair.Key, pair.Value))
                .OrderBy(stream => stream.StreamId == SessionLogStreamIds.AnsightSdk ? 0 : 1)
                .ThenBy(stream => stream.DisplayName, StringComparer.Ordinal)
                .ToArray();
        }

        var result = normalizedStreams
            .Select(stream => stream.Entries.Count > 0 || !entriesByStreamId.TryGetValue(stream.StreamId, out var entries)
                ? stream
                : CloneWithEntries(stream, entries))
            .ToList();
        var knownStreamIds = result.Select(stream => stream.StreamId).ToHashSet(StringComparer.Ordinal);
        result.AddRange(entriesByStreamId
            .Where(pair => !knownStreamIds.Contains(pair.Key))
            .Select(pair => CreateStreamForEntries(pair.Key, pair.Value)));
        return result;
    }

    private static SessionLogStream CreateStreamForEntries(string streamId, IReadOnlyList<LogEntry> entries)
        => new()
        {
            StreamId = streamId,
            Kind = streamId switch
            {
                SessionLogStreamIds.AnsightSdk => SessionLogStreamKinds.AnsightSdk,
                SessionLogStreamIds.AndroidLogcat => SessionLogStreamKinds.AndroidLogcat,
                SessionLogStreamIds.AppleUnifiedLog => SessionLogStreamKinds.AppleUnifiedLog,
                _ => SessionLogStreamKinds.Imported
            },
            DisplayName = streamId switch
            {
                SessionLogStreamIds.AnsightSdk => "Ansight SDK",
                SessionLogStreamIds.AndroidLogcat => "Android Logcat",
                SessionLogStreamIds.AppleUnifiedLog => "Apple Unified Log",
                _ => streamId
            },
            Status = SessionLogStreamStatuses.Completed,
            StartedUtc = entries.Min(entry => entry.TimestampUtc),
            EndedUtc = entries.Max(entry => entry.TimestampUtc),
            Entries = entries,
            TotalEntryCount = entries.Count
        };

    private static SessionLogStream CloneWithEntries(SessionLogStream stream, IReadOnlyList<LogEntry> entries)
        => new()
        {
            StreamId = stream.StreamId,
            Kind = stream.Kind,
            DisplayName = stream.DisplayName,
            Status = stream.Status,
            StartedUtc = stream.StartedUtc,
            EndedUtc = stream.EndedUtc,
            StatusMessage = stream.StatusMessage,
            Metadata = stream.Metadata,
            Entries = entries,
            TotalEntryCount = Math.Max(stream.TotalEntryCount, entries.Count),
            RetainedEntryStartIndex = 0
        };

    private static string NormalizeStreamId(string? streamId)
        => string.IsNullOrWhiteSpace(streamId) ? SessionLogStreamIds.AnsightSdk : streamId.Trim();

    public static IReadOnlyList<LogEntry> Flatten(IReadOnlyList<SessionLogStream> streams)
    {
        if (streams.Count == 0)
        {
            return Array.Empty<LogEntry>();
        }

        var queue = new PriorityQueue<SessionLogCursor, SessionLogSortKey>();
        var totalEntryCount = 0;
        for (var streamIndex = 0; streamIndex < streams.Count; streamIndex++)
        {
            var stream = streams[streamIndex];
            var entries = EnsureTimestampOrder(stream.Entries);
            totalEntryCount += entries.Count;
            if (entries.Count == 0)
            {
                continue;
            }

            queue.Enqueue(
                new SessionLogCursor(stream.StreamId, entries, streamIndex, 0),
                new SessionLogSortKey(entries[0].TimestampUtc, streamIndex, 0));
        }

        if (totalEntryCount == 0)
        {
            return Array.Empty<LogEntry>();
        }

        var result = new List<LogEntry>(totalEntryCount);
        while (queue.TryDequeue(out var cursor, out _))
        {
            result.Add(WithStreamId(cursor.Entries[cursor.EntryIndex], cursor.StreamId));
            var nextEntryIndex = cursor.EntryIndex + 1;
            if (nextEntryIndex >= cursor.Entries.Count)
            {
                continue;
            }

            queue.Enqueue(
                cursor with { EntryIndex = nextEntryIndex },
                new SessionLogSortKey(
                    cursor.Entries[nextEntryIndex].TimestampUtc,
                    cursor.StreamIndex,
                    nextEntryIndex));
        }

        return result.ToArray();
    }

    public static LogEntry WithStreamId(LogEntry entry, string streamId)
        => string.Equals(entry.StreamId, streamId, StringComparison.Ordinal)
            ? entry
            : entry with { StreamId = streamId };

    private static SessionLogStream CloneNormalized(SessionLogStream stream)
    {
        var streamId = stream.StreamId.Trim();
        var sourceEntries = stream.Entries ?? Array.Empty<LogEntry>();
        var entries = sourceEntries.All(entry => string.Equals(entry.StreamId, streamId, StringComparison.Ordinal))
            ? sourceEntries
            : sourceEntries.Select(entry => WithStreamId(entry, streamId)).ToArray();
        return new SessionLogStream
        {
            StreamId = streamId,
            Kind = string.IsNullOrWhiteSpace(stream.Kind) ? SessionLogStreamKinds.Imported : stream.Kind.Trim(),
            DisplayName = string.IsNullOrWhiteSpace(stream.DisplayName) ? streamId : stream.DisplayName.Trim(),
            Status = string.IsNullOrWhiteSpace(stream.Status) ? SessionLogStreamStatuses.Completed : stream.Status.Trim(),
            StartedUtc = stream.StartedUtc,
            EndedUtc = stream.EndedUtc,
            StatusMessage = stream.StatusMessage,
            Metadata = stream.Metadata is null
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : new Dictionary<string, string>(stream.Metadata, StringComparer.Ordinal),
            Entries = entries,
            TotalEntryCount = Math.Max(stream.TotalEntryCount, entries.Count),
            RetainedEntryStartIndex = stream.RetainedEntryStartIndex
        };
    }

    private static IReadOnlyList<LogEntry> EnsureTimestampOrder(IReadOnlyList<LogEntry> entries)
    {
        for (var index = 1; index < entries.Count; index++)
        {
            if (entries[index - 1].TimestampUtc > entries[index].TimestampUtc)
            {
                return entries.OrderBy(entry => entry.TimestampUtc).ToArray();
            }
        }

        return entries;
    }

    private static bool HaveMatchingEntryCounts(
        IReadOnlyList<SessionLogStream> streams,
        IReadOnlyList<LogEntry> logs)
    {
        var streamEntryCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var totalEntryCount = 0;
        foreach (var stream in streams)
        {
            totalEntryCount += stream.Entries.Count;
            streamEntryCounts.TryGetValue(stream.StreamId, out var existingCount);
            streamEntryCounts[stream.StreamId] = existingCount + stream.Entries.Count;
        }

        if (totalEntryCount != logs.Count)
        {
            return false;
        }

        foreach (var log in logs)
        {
            var streamId = NormalizeStreamId(log.StreamId);
            if (!streamEntryCounts.TryGetValue(streamId, out var remainingCount) || remainingCount == 0)
            {
                return false;
            }

            streamEntryCounts[streamId] = remainingCount - 1;
        }

        return streamEntryCounts.Values.All(count => count == 0);
    }

    private readonly record struct SessionLogCursor(
        string StreamId,
        IReadOnlyList<LogEntry> Entries,
        int StreamIndex,
        int EntryIndex);

    private readonly record struct SessionLogSortKey(
        DateTimeOffset TimestampUtc,
        int StreamIndex,
        int EntryIndex) : IComparable<SessionLogSortKey>
    {
        public int CompareTo(SessionLogSortKey other)
        {
            var timestampComparison = TimestampUtc.CompareTo(other.TimestampUtc);
            if (timestampComparison != 0)
            {
                return timestampComparison;
            }

            var streamComparison = StreamIndex.CompareTo(other.StreamIndex);
            return streamComparison != 0 ? streamComparison : EntryIndex.CompareTo(other.EntryIndex);
        }
    }
}
