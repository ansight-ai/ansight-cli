using System.IO.Compression;

namespace Ansight.Host.Activity;

internal static class SessionActivityArchive
{
    internal const string EntryPath = "session-data/activity.jsonl";
    private const long MaximumImportBytes = 40 * 1024 * 1024;

    internal static void Write(ZipArchive archive, SessionActivityService service, string sessionId, bool sanitized)
    {
        // Caller and work identifiers are intentionally omitted from sanitized exports.
        if (sanitized) return;
        using var writer = new StreamWriter(archive.CreateEntry(EntryPath, CompressionLevel.Optimal).Open(), new UTF8Encoding(false));
        string? cursor = null;
        var incomplete = false;
        while (true)
        {
            var page = service.Read(sessionId, cursor, 500);
            incomplete |= page.RetentionLimited || page.CursorExpired || page.SkippedRecords > 0 || page.IncompleteTail || page.PersistenceError is not null || page.ImportedHistoryIncomplete;
            foreach (var record in page.Records) writer.WriteLine(JsonSerializer.Serialize(record, SessionActivityStore.JsonOptions));
            if (page.Records.Count < 500 || page.NextCursor == cursor) break;
            cursor = page.NextCursor;
        }
        writer.Dispose();
        using var status = new StreamWriter(archive.CreateEntry("session-data/activity-status.json").Open());
        status.Write(JsonSerializer.Serialize(new { schema = "ansight.session-activity-export/v1", incomplete }, SessionActivityStore.JsonOptions));
    }

    internal static IReadOnlyList<SessionActivityRecord> Read(ISessionArchiveReader archive, string sourceSessionId)
    {
        var entry = archive.GetEntry(EntryPath);
        if (entry is null) return [];
        if (entry.Length < 0 || entry.Length > MaximumImportBytes) throw new InvalidDataException("The activity journal exceeds the archive import limit.");
        var sourceIncomplete = false;
        if (archive.GetEntry("session-data/activity-status.json") is { } statusEntry)
        {
            if (statusEntry.Length > 1024) throw new InvalidDataException("Invalid activity export status.");
            using var statusStream = statusEntry.Open();
            var statusBytes = new byte[1025];
            var statusLength = statusStream.ReadAtLeast(statusBytes, statusBytes.Length, throwOnEndOfStream: false);
            if (statusLength == statusBytes.Length) throw new InvalidDataException("Invalid activity export status.");
            using var status = JsonDocument.Parse(statusBytes.AsMemory(0, statusLength));
            if (status.RootElement.ValueKind != JsonValueKind.Object || !status.RootElement.TryGetProperty("incomplete", out var value)
                || value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new InvalidDataException("Invalid activity export status.");
            sourceIncomplete = value.GetBoolean();
        }
        using var stream = entry.Open();
        using var reader = new StreamReader(stream);
        var records = new List<SessionActivityRecord>();
        long read = 0;
        while (ReadLine(reader, ref read) is { } line)
        {
            if (line.Length == 0) continue;
            SessionActivityRecord? record;
            try { record = JsonSerializer.Deserialize<SessionActivityRecord>(line, SessionActivityStore.JsonOptions); }
            catch (JsonException) { throw new InvalidDataException("The archive contains invalid activity metadata."); }
            if (record is null || record.Schema != "ansight.session-activity/v1" || record.SessionId != sourceSessionId
                || record.Caller is null || record.Phase is not ("started" or "target" or "completed"))
                throw new InvalidDataException("The archive contains invalid activity metadata.");
            records.Add(record with { HistoryIncomplete = sourceIncomplete || record.HistoryIncomplete });
        }
        return records;
    }

    internal static void Import(IReadOnlyList<SessionActivityRecord> records, SessionActivityService service, string sessionId)
    {
        var importId = Guid.NewGuid().ToString("N");
        foreach (var record in records)
        {
            service.Append(record with
            {
                EventId = Guid.NewGuid().ToString("N"), SessionId = sessionId, Provenance = "imported",
                // Namespace imported IDs so an archive can never complete/spoof a local operation.
                OperationId = importId + ":" + ActivityCaller.Limit(record.OperationId),
                ParentOperationId = record.ParentOperationId is null ? null : importId + ":" + ActivityCaller.Limit(record.ParentOperationId),
                TraceId = importId + ":" + ActivityCaller.Limit(record.TraceId),
                Caller = ActivityCaller.Declared(record.Caller) with
                {
                    PrincipalId = ActivityCaller.Limit(record.Caller.PrincipalId), PrincipalAttribution = "imported",
                    ExecutorAttribution = "imported", Ingress = "import", InitiatorKind = record.Caller.InitiatorKind,
                    InitiatorName = ActivityCaller.Limit(record.Caller.InitiatorName), InitiatorAttribution = "imported"
                }
            });
        }
    }

    private static string? ReadLine(TextReader reader, ref long read)
    {
        var line = new StringBuilder();
        while (reader.Read() is var value && value >= 0)
        {
            if (++read > MaximumImportBytes || line.Length > SessionActivityStore.MaximumRecordBytes)
                throw new InvalidDataException("The activity journal exceeds the archive import limit.");
            if (value == '\n') return line.ToString();
            line.Append((char)value);
        }
        return line.Length == 0 ? null : throw new InvalidDataException("The archive contains an incomplete activity record.");
    }
}
