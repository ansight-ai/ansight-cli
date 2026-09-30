using System.Globalization;
using System.IO.Compression;

namespace Ansight.Host.Runtime.Archives;

internal static class SessionArchiveVisualTreeCodec
{
    private const string ArchiveDirectoryName = "visual-trees";

    public static IReadOnlyList<SessionVisualTreeSnapshotIndexEntry> Write(
        ZipArchive archive,
        IReadOnlyList<SessionVisualTreeSnapshot> snapshots,
        DateTimeOffset savedAtUtc,
        JsonSerializerOptions jsonOptions)
    {
        ArgumentNullException.ThrowIfNull(archive);
        ArgumentNullException.ThrowIfNull(snapshots);
        ArgumentNullException.ThrowIfNull(jsonOptions);

        var index = new List<SessionVisualTreeSnapshotIndexEntry>(snapshots.Count);
        foreach (var pair in snapshots
                     .Where(static snapshot => !string.IsNullOrWhiteSpace(snapshot.SnapshotId))
                     .OrderBy(static snapshot => snapshot.CapturedAtUtc)
                     .ThenBy(static snapshot => snapshot.SnapshotId, StringComparer.Ordinal)
                     .Select(static (snapshot, position) => new { Snapshot = snapshot, Position = position }))
        {
            var entryPath = BuildEntryPath(pair.Snapshot, pair.Position);
            var header = SessionVisualTreeSnapshotHeader.Create(pair.Snapshot);
            var entry = archive.CreateEntry(entryPath, CompressionLevel.Optimal);
            using (var stream = entry.Open())
            {
                JsonSerializer.Serialize(
                    stream,
                    new SessionVisualTreeSnapshotDocument
                    {
                        SavedAtUtc = savedAtUtc,
                        Header = header,
                        Snapshot = pair.Snapshot
                    },
                    jsonOptions);
            }

            index.Add(new SessionVisualTreeSnapshotIndexEntry
            {
                EntryPath = entryPath,
                Header = header
            });
        }

        return index;
    }

    public static IReadOnlyList<SessionVisualTreeSnapshot> Read(
        ISessionArchiveReader archive,
        IReadOnlyList<SessionVisualTreeSnapshotIndexEntry> index)
    {
        ArgumentNullException.ThrowIfNull(archive);
        ArgumentNullException.ThrowIfNull(index);
        if (index.Count == 0)
        {
            return [];
        }

        var snapshots = new List<SessionVisualTreeSnapshot>(index.Count);
        foreach (var descriptor in index)
        {
            var entryPath = NormalizeEntryPath(descriptor.EntryPath);
            if (string.IsNullOrWhiteSpace(entryPath) || IsUnsafeEntryPath(entryPath))
            {
                continue;
            }

            var entry = archive.GetEntry(entryPath);
            if (entry is null)
            {
                continue;
            }

            try
            {
                using var stream = entry.Open();
                var document = JsonSerializer.Deserialize<SessionVisualTreeSnapshotDocument>(stream, jsonOptions);
                if (document?.Snapshot is null
                    || !string.Equals(document.Schema, SessionVisualTreeSnapshotDocument.SchemaName, StringComparison.Ordinal)
                    || !string.Equals(document.Snapshot.SnapshotId, descriptor.Header.SnapshotId, StringComparison.Ordinal))
                {
                    continue;
                }

                snapshots.Add(document.Snapshot);
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or JsonException)
            {
                // One malformed external tree should not prevent the rest of the session from importing.
            }
        }

        return snapshots
            .GroupBy(static snapshot => snapshot.SnapshotId, StringComparer.Ordinal)
            .Select(static group => group.OrderByDescending(static snapshot => snapshot.CapturedAtUtc).First())
            .OrderBy(static snapshot => snapshot.CapturedAtUtc)
            .ThenBy(static snapshot => snapshot.SnapshotId, StringComparer.Ordinal)
            .ToArray();
    }

    private static readonly JsonSerializerOptions jsonOptions = new()
    {
        MaxDepth = JsonUtil.MaximumDepth,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private static string BuildEntryPath(SessionVisualTreeSnapshot snapshot, int position)
    {
        var safeSnapshotId = string.Concat(snapshot.SnapshotId
            .Trim()
            .Select(static character => char.IsLetterOrDigit(character) || character is '-' or '_' ? character : '-'))
            .Trim('-');
        if (safeSnapshotId.Length == 0)
        {
            safeSnapshotId = "snapshot";
        }
        else if (safeSnapshotId.Length > 80)
        {
            safeSnapshotId = safeSnapshotId[..80];
        }

        var timestamp = snapshot.CapturedAtUtc
            .ToUniversalTime()
            .ToString("yyyyMMddHHmmssfffffff", CultureInfo.InvariantCulture);
        return $"{ArchiveDirectoryName}/{timestamp}-{position:D6}-{safeSnapshotId}.json";
    }

    private static string NormalizeEntryPath(string entryPath)
        => entryPath.Trim().Replace('\\', '/').TrimStart('/');

    private static bool IsUnsafeEntryPath(string entryPath)
        => entryPath.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Any(static segment => string.Equals(segment, "..", StringComparison.Ordinal));
}
