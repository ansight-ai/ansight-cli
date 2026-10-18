using System.Globalization;
using System.Text.Json.Serialization;
using Ansight.Infrastructure.Security;

namespace Ansight.Host.Activity;

/// <summary>Append-only, per-session JSONL segments, outside the recording so deletion remains auditable.</summary>
internal sealed class SessionActivityStore
{
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        MaxDepth = 16
    };
    internal const int MaximumRecordBytes = 32 * 1024;
    private readonly string root;
    private readonly long segmentBytes;
    private readonly int segmentCount;
    private string? persistenceError;
    internal string? PersistenceError => Volatile.Read(ref persistenceError);

    internal SessionActivityStore(string dataDirectory, long segmentBytes = 4 * 1024 * 1024, int segmentCount = 8)
    {
        root = Path.Combine(dataDirectory, "session-activity");
        this.segmentBytes = segmentBytes;
        this.segmentCount = segmentCount;
    }

    internal string DirectoryFor(string? sessionId) => Path.Combine(root,
        sessionId is null ? "host" : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sessionId))));

    internal bool Append(SessionActivityRecord record)
    {
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(record, JsonOptions);
            if (bytes.Length > MaximumRecordBytes) throw new InvalidDataException("Activity record exceeds the metadata limit.");
            var directory = DirectoryFor(record.SessionId);
            PrivateStorageDirectory.Ensure(directory);
            using var writeLock = AcquireLock(directory);
            var files = Segments(directory);
            var number = files.Length == 0 ? 1 : SegmentNumber(files[^1]);
            var path = Path.Combine(directory, $"{number:D12}.jsonl");
            if (File.Exists(path) && new FileInfo(path).Length + bytes.Length + 1 > segmentBytes)
                path = Path.Combine(directory, $"{++number:D12}.jsonl");
            using (var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read))
            {
                Restrict(path);
                // Preserve a torn line for the reader to report, but never concatenate the next event to it.
                if (stream.Length > 0)
                {
                    stream.Position = stream.Length - 1;
                    if (stream.ReadByte() != '\n') stream.WriteByte((byte)'\n');
                }
                stream.Position = stream.Length;
                stream.Write(bytes);
                stream.WriteByte((byte)'\n');
                stream.Flush(flushToDisk: true);
            }
            foreach (var old in Segments(directory).SkipLast(segmentCount)) File.Delete(old);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Sticky for this runtime: a later successful write must not hide a gap.
            var warning = "Some session activity could not be persisted (" + exception.GetType().Name + ").";
            if (Interlocked.CompareExchange(ref persistenceError, warning, null) is null)
            {
                System.Diagnostics.Trace.TraceError(warning);
                Console.Error.WriteLine("Warning: " + warning);
            }
            return false;
        }
    }

    internal SessionActivityPage Read(string? sessionId, string? cursor = null, int limit = 200)
    {
        if (limit is < 1 or > 500) throw new ArgumentOutOfRangeException(nameof(limit), "Activity limit must be between 1 and 500.");
        long requestedSegment = 0;
        long requestedOffset = 0;
        if (cursor is not null)
        {
            var parts = cursor.Split(':');
            if (parts.Length != 2 || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out requestedSegment)
                || !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out requestedOffset)
                || requestedSegment < 1 || requestedOffset < 0)
                throw new ArgumentException("Invalid activity cursor.", nameof(cursor));
        }
        var directory = DirectoryFor(sessionId);
        if (!Directory.Exists(directory)) return new([], null, false, cursor is not null, 0, false, PersistenceError);
        using var readLock = AcquireLock(directory);
        var files = Segments(directory);
        var first = files.Length == 0 ? 0 : SegmentNumber(files[0]);
        var expired = cursor is not null && (requestedSegment < first || !files.Any(path => SegmentNumber(path) == requestedSegment));
        if (expired) { requestedSegment = 0; requestedOffset = 0; }
        var records = new List<SessionActivityRecord>();
        var skipped = 0;
        var incomplete = false;
        string? next = null;
        foreach (var path in files)
        {
            var number = SegmentNumber(path);
            if (number < requestedSegment) continue;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (number == requestedSegment)
            {
                if (requestedOffset > stream.Length) throw new ArgumentException("Activity cursor exceeds the segment length.", nameof(cursor));
                stream.Position = requestedOffset;
            }
            // Byte offsets remain stable across UTF-8 text and appends. Bounded segments bound the scan.
            var lineStart = stream.Position;
            using var line = new MemoryStream();
            while (stream.Position < stream.Length)
            {
                var value = stream.ReadByte();
                if (value != '\n')
                {
                    if (line.Length <= MaximumRecordBytes) line.WriteByte((byte)value);
                    continue;
                }
                try
                {
                    var record = line.Length > MaximumRecordBytes ? null : JsonSerializer.Deserialize<SessionActivityRecord>(line.GetBuffer().AsSpan(0, (int)line.Length), JsonOptions);
                    if (record is null || record.Schema != "ansight.session-activity/v1" || record.SessionId != sessionId
                        || record.Caller is null || record.Phase is not ("started" or "target" or "completed")) skipped++;
                    else records.Add(record);
                }
                catch (JsonException) { skipped++; }
                line.SetLength(0);
                lineStart = stream.Position;
                next = $"{number}:{stream.Position}";
                if (records.Count >= limit) return new(records, next, first > 1, expired, skipped, false, PersistenceError, records.Any(record => record.HistoryIncomplete));
            }
            if (line.Length > 0)
            {
                incomplete = true;
                next = $"{number}:{lineStart}";
            }
            else next = $"{number}:{stream.Position}";
        }
        return new(records, next, first > 1, expired, skipped, incomplete, PersistenceError, records.Any(record => record.HistoryIncomplete));
    }

    private static FileStream AcquireLock(string directory)
    {
        var deadline = Environment.TickCount64 + 3000;
        while (true)
        {
            try
            {
                // FileShare.None also excludes cooperating writers in other CLI/host processes.
                var stream = new FileStream(Path.Combine(directory, ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                Restrict(Path.Combine(directory, ".lock"));
                return stream;
            }
            catch (IOException) when (Environment.TickCount64 < deadline) { Thread.Sleep(10); }
        }
    }

    private static void Restrict(string path)
    {
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private static string[] Segments(string directory) => Directory.GetFiles(directory, "*.jsonl")
        .Where(path => Path.GetFileNameWithoutExtension(path).Length == 12 && long.TryParse(Path.GetFileNameWithoutExtension(path), out _))
        .Order(StringComparer.Ordinal).ToArray();
    private static long SegmentNumber(string path) => long.Parse(Path.GetFileNameWithoutExtension(path), CultureInfo.InvariantCulture);
}
