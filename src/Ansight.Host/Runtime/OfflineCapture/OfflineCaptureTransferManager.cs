using System.IO.Compression;
using System.Text.Json.Nodes;
using Ansight.Host.Runtime.BinaryTransfers;

namespace Ansight.Host.Runtime.OfflineCapture;

/// <summary>Receives archives only on an authenticated app WebSocket. A receipt is
/// produced only after the existing session store has persisted the imported evidence.</summary>
internal sealed class OfflineCaptureTransferManager(IRuntimeState runtimeState) : IDisposable
{
    internal const string BeginAction = "offline_capture.begin";
    internal const string CommitAction = "offline_capture.commit";
    private const long MaximumArchiveBytes = 1024L * 1024 * 1024;
    private const string ReceiptGroup = "ansight.offlineCapture";
    private readonly Lock gate = new();
    private readonly Dictionary<string, Transfer> transfers = new(StringComparer.Ordinal);

    internal void Begin(string connectionSessionId, JsonObject? payload)
    {
        var descriptor = Descriptor.Parse(payload);
        lock (gate)
        {
            // One bounded transfer per authenticated app connection. A retry replaces
            // an abandoned transfer, and disconnect removes its temporary content.
            CancelSession(connectionSessionId);
            var file = Path.Combine(Path.GetTempPath(), "ansight-offline-" + Guid.NewGuid().ToString("N") + ".part");
            var stream = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            transfers.Add(connectionSessionId, new Transfer(descriptor, file, stream));
        }
    }

    internal bool TryHandleBinaryMessage(string connectionSessionId, ReadOnlyMemory<byte> payload)
    {
        if (!BinaryFileTransferProtocol.HasMagic(payload.Span)) return false;
        if (!BinaryFileTransferProtocol.TryParseFrame(payload, out var frame, out var error))
            throw new InvalidDataException(error);
        lock (gate)
        {
            if (!transfers.TryGetValue(connectionSessionId, out var transfer) ||
                transfer.Descriptor.TransferId != frame.Header.TransferId) return false;
            try
            {
                if (transfer.Completed || frame.Header.Sequence != transfer.Sequence || frame.Header.OffsetBytes != transfer.Bytes)
                    throw new InvalidDataException("Offline capture frame sequence or offset is invalid.");
                switch (frame.Header.FrameType)
                {
                    case BinaryFileTransferFrameType.Chunk:
                        if (frame.Payload.Length > 1024 * 1024 || frame.Payload.Length > transfer.Descriptor.ByteCount - transfer.Bytes)
                            throw new InvalidDataException("Offline capture exceeds its declared size or chunk limit.");
                        transfer.Stream.Write(frame.Payload.Span);
                        transfer.Bytes += frame.Payload.Length;
                        transfer.Sequence++;
                        break;
                    case BinaryFileTransferFrameType.Complete:
                        if (!frame.Payload.IsEmpty || transfer.Bytes != transfer.Descriptor.ByteCount)
                            throw new InvalidDataException("Offline capture completed with an incorrect length.");
                        transfer.Stream.Flush(flushToDisk: true);
                        transfer.Stream.Dispose();
                        transfer.Completed = true;
                        break;
                    default: throw new InvalidDataException("Offline capture transfer was aborted.");
                }
            }
            catch
            {
                CancelSession(connectionSessionId);
                throw;
            }
            return true;
        }
    }

    internal JsonObject Commit(string connectionSessionId, JsonObject? payload)
    {
        var descriptor = Descriptor.Parse(payload);
        lock (gate)
        {
            if (!transfers.TryGetValue(connectionSessionId, out var transfer) || transfer.Descriptor != descriptor || !transfer.Completed)
                throw new InvalidDataException("Offline capture transfer is incomplete or does not match its descriptor.");
            try
            {
                using (var input = File.OpenRead(transfer.Path))
                {
                    var digest = Convert.ToHexStringLower(SHA256.HashData(input));
                    if (digest != descriptor.Sha256) throw new InvalidDataException("Offline capture checksum does not match.");
                }
                using var zip = ZipFile.OpenRead(transfer.Path);
                ValidateArchive(zip);
                using var reader = new SystemZipSessionArchiveReader(zip);
                if (!SessionArchiveExternalPayloadReader.TryReadJsonLinesArchive(reader, descriptor.SessionId + ".zip", out var capture, out var error) || capture is null)
                    throw new InvalidDataException(error ?? "Offline capture could not be decoded.");
                if (capture.Snapshot.SessionId != descriptor.SessionId)
                    throw new InvalidDataException("Offline capture session identity does not match its descriptor.");
                if (!runtimeState.TryGetSessionSnapshot(connectionSessionId, out var live) || live?.AppId != capture.Snapshot.AppId)
                    throw new InvalidDataException("Offline capture belongs to a different app than the authenticated connection.");

                // Stable across reconnects and host restarts. The hash and source ID are
                // persisted with the snapshot before acknowledgement, so a lost receipt
                // cannot create another imported session.
                var importedId = "offline-" + descriptor.SessionId;
                if (runtimeState.TryGetSessionSnapshot(importedId, out var previous) && previous is not null)
                {
                    if (previous.AppId != capture.Snapshot.AppId ||
                        previous.CustomProperties?[ReceiptGroup]?["sha256"]?.GetValue<string>() != descriptor.Sha256)
                        throw new InvalidDataException("Offline capture identity already exists with different content.");
                    return Receipt(descriptor, importedId);
                }
                var document = JsonSerializer.SerializeToNode(capture.Snapshot, JsonUtil.Compact)!.AsObject();
                document["sessionId"] = importedId;
                var properties = document["customProperties"] as JsonObject ?? new JsonObject();
                if (document["customProperties"] is null) document["customProperties"] = properties;
                properties[ReceiptGroup] = new JsonObject { ["sessionId"] = descriptor.SessionId, ["sha256"] = descriptor.Sha256 };
                var snapshot = document.Deserialize<AppSessionSnapshot>(JsonUtil.Compact)!;
                var result = runtimeState.ImportSessionSnapshot(snapshot, capture.ImageBytesByFrameId,
                    capture.AppIconBytes, capture.ArtifactBytesByRelativePath);
                if (!result.IsSuccess) throw new IOException(result.Message);
                if (result.ImportedSession?.SessionId != importedId)
                    throw new IOException("Offline import identity changed; acknowledgement withheld.");
                return Receipt(descriptor, importedId);
            }
            finally { CancelSession(connectionSessionId); }
        }
    }

    private static JsonObject Receipt(Descriptor descriptor, string importedId) => new()
    {
        ["sessionId"] = descriptor.SessionId, ["sha256"] = descriptor.Sha256,
        ["importedSessionId"] = importedId, ["durable"] = true
    };

    private static void ValidateArchive(ZipArchive zip)
    {
        if (zip.Entries.Count > 65_535) throw new InvalidDataException("Offline archive has too many entries.");
        long bytes = 0;
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in zip.Entries)
        {
            bytes = checked(bytes + entry.Length);
            if (bytes > MaximumArchiveBytes * 2 || entry.FullName.Contains('\\') || entry.FullName.StartsWith('/') ||
                entry.FullName.Split('/').Any(segment => segment is ".." or ".") || !paths.Add(entry.FullName))
                throw new InvalidDataException("Offline archive contains unsafe, duplicate, or oversized entries.");
        }
    }

    internal void CancelSession(string sessionId)
    {
        lock (gate)
        {
            if (!transfers.Remove(sessionId, out var transfer)) return;
            transfer.Stream.Dispose();
            File.Delete(transfer.Path);
        }
    }

    public void Dispose()
    {
        lock (gate) { foreach (var session in transfers.Keys.ToArray()) CancelSession(session); }
    }

    private sealed record Descriptor(string SessionId, string TransferId, long ByteCount, string Sha256)
    {
        internal static Descriptor Parse(JsonObject? payload)
        {
            var sessionId = payload?["sessionId"]?.GetValue<string>() ?? "";
            var transferId = payload?["transferId"]?.GetValue<string>() ?? "";
            var hash = payload?["sha256"]?.GetValue<string>() ?? "";
            var bytes = payload?["byteCount"]?.GetValue<long>() ?? 0;
            if (payload?["schema"]?.GetValue<string>() != "ansight.offline-transfer.v1" ||
                (!Guid.TryParseExact(sessionId, "D", out _) && !Guid.TryParseExact(sessionId, "N", out _)) || !Guid.TryParseExact(transferId, "N", out _) ||
                bytes is <= 0 or > MaximumArchiveBytes || hash.Length != 64 || hash.Any(c => !char.IsAsciiHexDigitLower(c)))
                throw new InvalidDataException("Invalid offline capture transfer descriptor.");
            return new Descriptor(sessionId, transferId, bytes, hash);
        }
    }

    private sealed class Transfer(Descriptor descriptor, string path, FileStream stream)
    {
        internal Descriptor Descriptor { get; } = descriptor;
        internal string Path { get; } = path;
        internal FileStream Stream { get; } = stream;
        internal int Sequence { get; set; }
        internal long Bytes { get; set; }
        internal bool Completed { get; set; }
    }
}
