namespace Ansight.Host.Runtime.Archives;

using System.IO.Compression;
using System.Text.Json;
using Ansight.Host;

internal static class SessionLogStreamArchiveCodec
{
    public const string IndexEntryPath = "session-data/log-streams/index.json";

    public static void Write(
        ZipArchive archive,
        IReadOnlyList<SessionLogStream> sourceStreams,
        DateTimeOffset savedAtUtc,
        JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(archive);
        var streams = SessionLogStreams.Normalize(sourceStreams, Array.Empty<LogEntry>());
        var descriptors = new List<SessionLogStreamArchiveDescriptor>(streams.Count);
        for (var index = 0; index < streams.Count; index++)
        {
            var stream = streams[index];
            var entryPath = $"session-data/log-streams/{index:D3}-{SanitizePathSegment(stream.StreamId)}.json";
            descriptors.Add(SessionLogStreamArchiveDescriptor.FromStream(stream, entryPath));
            WriteJsonEntry(
                archive,
                entryPath,
                new SessionLogStreamArchiveEntriesDocument
                {
                    SavedAtUtc = savedAtUtc,
                    StreamId = stream.StreamId,
                    Entries = stream.Entries
                },
                options);
        }

        WriteJsonEntry(
            archive,
            IndexEntryPath,
            new SessionLogStreamArchiveIndexDocument
            {
                SavedAtUtc = savedAtUtc,
                Streams = descriptors
            },
            options);
    }

    public static IReadOnlyList<SessionLogStream>? TryRead(
        ZipArchive archive,
        JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(archive);
        using var reader = new SystemZipSessionArchiveReader(archive);
        return TryRead(reader, options);
    }

    public static IReadOnlyList<SessionLogStream>? TryRead(
        ISessionArchiveReader archive,
        JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(archive);
        var indexEntry = archive.GetEntry(IndexEntryPath);
        if (indexEntry is null)
        {
            return null;
        }

        SessionLogStreamArchiveIndexDocument? index;
        using (var stream = indexEntry.Open())
        {
            index = JsonSerializer.Deserialize<SessionLogStreamArchiveIndexDocument>(stream, options);
        }

        if (index?.Streams is not { Count: > 0 })
        {
            return Array.Empty<SessionLogStream>();
        }

        var result = new List<SessionLogStream>(index.Streams.Count);
        foreach (var descriptor in index.Streams)
        {
            if (string.IsNullOrWhiteSpace(descriptor.StreamId)
                || string.IsNullOrWhiteSpace(descriptor.EntryPath))
            {
                continue;
            }

            var entriesDocument = ReadEntriesDocument(archive, descriptor.EntryPath, options);
            result.Add(new SessionLogStream
            {
                StreamId = descriptor.StreamId,
                Kind = descriptor.Kind,
                DisplayName = descriptor.DisplayName,
                Status = descriptor.Status,
                StartedUtc = descriptor.StartedUtc,
                EndedUtc = descriptor.EndedUtc,
                StatusMessage = descriptor.StatusMessage,
                Metadata = descriptor.Metadata ?? new Dictionary<string, string>(StringComparer.Ordinal),
                Entries = (entriesDocument?.Entries ?? Array.Empty<LogEntry>())
                    .Select(entry => SessionLogStreams.WithStreamId(entry, descriptor.StreamId))
                    .ToArray(),
                TotalEntryCount = Math.Max(descriptor.TotalEntryCount, descriptor.EntryCount),
                RetainedEntryStartIndex = 0
            });
        }

        return result;
    }

    public static IReadOnlyList<LogEntry> GetLegacySdkEntries(IReadOnlyList<SessionLogStream> streams)
        => streams
            .Where(stream => string.Equals(stream.StreamId, SessionLogStreamIds.AnsightSdk, StringComparison.Ordinal)
                             || string.Equals(stream.Kind, SessionLogStreamKinds.AnsightSdk, StringComparison.Ordinal))
            .SelectMany(stream => stream.Entries)
            .OrderBy(entry => entry.TimestampUtc)
            .ToArray();

    private static SessionLogStreamArchiveEntriesDocument? ReadEntriesDocument(
        ISessionArchiveReader archive,
        string entryPath,
        JsonSerializerOptions options)
    {
        var entry = archive.GetEntry(entryPath);
        if (entry is null)
        {
            return null;
        }

        using var stream = entry.Open();
        return JsonSerializer.Deserialize<SessionLogStreamArchiveEntriesDocument>(stream, options);
    }

    private static void WriteJsonEntry<T>(
        ZipArchive archive,
        string entryPath,
        T document,
        JsonSerializerOptions options)
    {
        var entry = archive.CreateEntry(entryPath, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open());
        writer.Write(JsonSerializer.Serialize(document, options));
    }

    private static string SanitizePathSegment(string streamId)
    {
        var characters = streamId.Trim()
            .Select(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_'
                ? char.ToLowerInvariant(character)
                : '-')
            .ToArray();
        var value = new string(characters).Trim('-');
        return string.IsNullOrWhiteSpace(value) ? "stream" : value;
    }

    private sealed class SessionLogStreamArchiveIndexDocument
    {
        public const string SchemaName = "ansight.session-log-stream-index.v1";

        public string Schema { get; init; } = SchemaName;
        public required DateTimeOffset SavedAtUtc { get; init; }
        public required IReadOnlyList<SessionLogStreamArchiveDescriptor> Streams { get; init; }
    }

    private sealed class SessionLogStreamArchiveDescriptor
    {
        public required string StreamId { get; init; }
        public required string Kind { get; init; }
        public required string DisplayName { get; init; }
        public required string Status { get; init; }
        public DateTimeOffset? StartedUtc { get; init; }
        public DateTimeOffset? EndedUtc { get; init; }
        public string? StatusMessage { get; init; }
        public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);
        public required string EntryPath { get; init; }
        public int EntryCount { get; init; }
        public int TotalEntryCount { get; init; }

        public static SessionLogStreamArchiveDescriptor FromStream(SessionLogStream stream, string entryPath)
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
                EntryPath = entryPath,
                EntryCount = stream.Entries.Count,
                TotalEntryCount = Math.Max(stream.TotalEntryCount, stream.Entries.Count)
            };
    }

    private sealed class SessionLogStreamArchiveEntriesDocument
    {
        public const string SchemaName = "ansight.session-log-stream.v1";

        public string Schema { get; init; } = SchemaName;
        public required DateTimeOffset SavedAtUtc { get; init; }
        public required string StreamId { get; init; }
        public required IReadOnlyList<LogEntry> Entries { get; init; }
    }
}
