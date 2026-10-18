using System.Text.Json.Nodes;

namespace Ansight.Host.Runtime.BinaryTransfers;

/// <summary>Receives app-initiated artifacts on the authenticated session connection.</summary>
internal sealed class ArtifactPublishTransferManager : IDisposable
{
    internal const string BeginAction = "artifact.publish.begin";
    internal const string CommitAction = "artifact.publish.commit";
    internal const string AbortAction = "artifact.publish.abort";
    internal const string Schema = "ansight.artifact.publish.v1";
    internal const long MaximumSizeBytes = 512L * 1024 * 1024;
    private readonly Lock gate = new();
    private readonly Dictionary<TransferKey, Transfer> transfers = new();

    internal void Begin(string sessionId, JsonObject? payload)
    {
        if (payload?["schema"]?.GetValue<string>() != Schema ||
            payload["wireProtocol"]?.GetValue<string>() != "ansight.file-transfer.v1" ||
            payload["artifact"] is not JsonObject artifact)
            throw new InvalidDataException("Unsupported artifact publication descriptor.");
        if (payload.ToJsonString().Length > 64 * 1024) throw new InvalidDataException("Artifact metadata is too large.");
        var key = ReadKey(sessionId, payload);
        foreach (var name in new[] { "artifactId", "providerId", "name", "kind", "mimeType", "fileName" })
            if (string.IsNullOrWhiteSpace(artifact[name]?.GetValue<string>()))
                throw new InvalidDataException($"Artifact metadata requires '{name}'.");
        var size = artifact["sizeBytes"]?.GetValue<long>();
        if (size is < 0 or > MaximumSizeBytes) throw new InvalidDataException("Published artifacts must be at most 512 MiB.");
        lock (gate)
        {
            foreach (var expired in transfers.Where(item => DateTimeOffset.UtcNow - item.Value.StartedAt > TimeSpan.FromMinutes(10)).Select(item => item.Key).ToArray())
                Remove(expired);
            if (transfers.ContainsKey(key)) throw new InvalidDataException("Artifact transfer already exists.");
            if (transfers.Count >= 64 || transfers.Keys.Count(item => item.SessionId == sessionId) >= 4)
                throw new InvalidDataException("Too many pending artifact publications.");
            var path = Path.Combine(Path.GetTempPath(), $"ansight-publish-{Guid.NewGuid():N}.part");
            var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            transfers.Add(key, new Transfer((JsonObject)artifact.DeepClone(), size, path, stream));
        }
    }

    internal bool TryHandleBinaryMessage(string sessionId, ReadOnlyMemory<byte> payload)
    {
        if (!BinaryFileTransferProtocol.HasMagic(payload.Span)) return false;
        if (!BinaryFileTransferProtocol.TryParseFrame(payload, out var frame, out var error)) throw new InvalidDataException(error);
        var key = new TransferKey(sessionId, frame.Header.TransferId);
        lock (gate)
        {
            if (!transfers.TryGetValue(key, out var transfer)) return false;
            try
            {
                if (transfer.Completed || frame.Header.Sequence != transfer.Sequence || frame.Header.OffsetBytes != transfer.Bytes)
                    throw new InvalidDataException("Artifact frame sequence or offset is invalid.");
                switch (frame.Header.FrameType)
                {
                    case BinaryFileTransferFrameType.Chunk:
                        if (frame.Payload.Length > 512 * 1024 || frame.Payload.Length > (transfer.Size ?? MaximumSizeBytes) - transfer.Bytes)
                            throw new InvalidDataException("Artifact exceeds its declared size or transfer limit.");
                        transfer.Stream.Write(frame.Payload.Span);
                        transfer.Bytes += frame.Payload.Length;
                        transfer.Sequence++;
                        break;
                    case BinaryFileTransferFrameType.Complete:
                        if (!frame.Payload.IsEmpty || transfer.Size is { } size && transfer.Bytes != size)
                            throw new InvalidDataException("Artifact completed with an incorrect length.");
                        transfer.Stream.Flush(flushToDisk: true);
                        transfer.Stream.Dispose();
                        transfer.Completed = true;
                        break;
                    default: throw new InvalidDataException("Artifact transfer was aborted.");
                }
            }
            catch (Exception exception)
            {
                // Keep the failure for commit to report, without retaining partial data.
                transfer.Error = exception.Message;
                transfer.Stream.Dispose();
                File.Delete(transfer.Path);
            }
            return true;
        }
    }

    internal JsonObject Commit(string sessionId, JsonObject? payload,
        Func<string, BinaryToolArtifactTransferRegistration, long, string?> persist)
    {
        var key = ReadKey(sessionId, payload);
        lock (gate)
        {
            if (!transfers.TryGetValue(key, out var transfer)) throw new InvalidDataException("Artifact publication was not started on this session.");
            try
            {
                if (transfer.Error is not null) throw new InvalidDataException(transfer.Error);
                if (!transfer.Completed) throw new InvalidDataException("Artifact transfer is incomplete.");
                var metadata = transfer.Metadata;
                var capturedAt = DateTimeOffset.TryParse(metadata["createdAtUtc"]?.GetValue<string>(), out var date) ? date : transfer.StartedAt;
                var registration = new BinaryToolArtifactTransferRegistration(
                    sessionId, key.TransferId, transfer.Path, "artifact.publish",
                    metadata["name"]!.GetValue<string>(), metadata["kind"]!.GetValue<string>(),
                    metadata["mimeType"]!.GetValue<string>(), metadata["fileName"]!.GetValue<string>(),
                    metadata["providerId"]!.GetValue<string>(), metadata["artifactId"]!.GetValue<string>(),
                    capturedAt, true, Task.CompletedTask);
                var snapshotId = persist(sessionId, registration, transfer.Bytes);
                if (snapshotId is null) throw new IOException("The host could not persist the published artifact.");
                return new JsonObject { ["snapshotId"] = snapshotId, ["sizeBytes"] = transfer.Bytes };
            }
            finally { Remove(key); }
        }
    }

    internal void Abort(string sessionId, JsonObject? payload)
    {
        lock (gate) Remove(ReadKey(sessionId, payload));
    }

    internal void CancelSession(string sessionId)
    {
        lock (gate)
            foreach (var key in transfers.Keys.Where(key => key.SessionId == sessionId).ToArray()) Remove(key);
    }

    public void Dispose()
    {
        lock (gate) foreach (var key in transfers.Keys.ToArray()) Remove(key);
    }

    private void Remove(TransferKey key)
    {
        if (!transfers.Remove(key, out var transfer)) return;
        transfer.Stream.Dispose();
        File.Delete(transfer.Path);
    }

    private static TransferKey ReadKey(string sessionId, JsonObject? payload)
    {
        if (!Guid.TryParseExact(payload?["transferId"]?.GetValue<string>(), "N", out var id))
            throw new InvalidDataException("Artifact transferId must be a UUID without separators.");
        return new TransferKey(sessionId, id.ToString("N"));
    }

    private sealed record TransferKey(string SessionId, string TransferId);
    private sealed class Transfer(JsonObject metadata, long? size, string path, FileStream stream)
    {
        internal JsonObject Metadata { get; } = metadata;
        internal long? Size { get; } = size;
        internal string Path { get; } = path;
        internal FileStream Stream { get; } = stream;
        internal DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;
        internal long Bytes { get; set; }
        internal int Sequence { get; set; }
        internal bool Completed { get; set; }
        internal string? Error { get; set; }
    }
}
