using System.Text;
using System.Text.Json.Nodes;
using Ansight.Tools;

namespace Ansight.Host.Runtime.BinaryTransfers;

public sealed class BinaryFileDownloadManager : IDisposable
{
    public const string BeginBinaryDownloadToolId = "files.begin_binary_download";

    private readonly Lock gate = new();
    private readonly Dictionary<string, BinaryFileDownloadOperation> pendingByRequestId = new(StringComparer.Ordinal);
    private readonly Dictionary<TransferKey, ActiveBinaryFileDownload> activeByTransfer = new();
    private bool disposed;

    public BinaryFileDownloadOperation BeginDownload(BinaryFileDownloadOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ThrowIfDisposed();

        ArgumentException.ThrowIfNullOrWhiteSpace(options.SessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.SandboxPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.DestinationDirectoryPath);

        var chunkBytes = Math.Clamp(options.ChunkBytes, 1024, 512 * 1024);
        var requestId = string.IsNullOrWhiteSpace(options.RequestId)
            ? $"tool.download.{Guid.NewGuid():N}"
            : options.RequestId.Trim();
        var downloadId = string.IsNullOrWhiteSpace(options.DownloadId)
            ? requestId
            : options.DownloadId.Trim();

        var operation = new BinaryFileDownloadOperation(
            requestId,
            options.SessionId.Trim(),
            options.SandboxPath.Trim(),
            options.DestinationDirectoryPath.Trim(),
            options.SandboxRoot?.Trim(),
            downloadId,
            chunkBytes);

        lock (gate)
        {
            if (!pendingByRequestId.TryAdd(requestId, operation))
            {
                throw new InvalidOperationException($"A binary download with request id '{requestId}' is already registered.");
            }
        }

        return operation;
    }

    public bool TryHandleToolEnvelope(ToolProtocolEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ThrowIfDisposed();

        var requestId = envelope.ReplyTo;
        if (string.IsNullOrWhiteSpace(requestId))
        {
            return false;
        }

        BinaryFileDownloadOperation? operation;
        lock (gate)
        {
            if (!pendingByRequestId.TryGetValue(requestId, out operation))
            {
                return false;
            }
        }

        return envelope.Type switch
        {
            ToolProtocolMessageTypes.ResultType => HandleToolResultEnvelope(operation!, envelope),
            ToolProtocolMessageTypes.ErrorType => HandleToolErrorEnvelope(operation!, envelope),
            _ => false
        };
    }

    public bool TryHandleBinaryMessage(string sessionId, ReadOnlyMemory<byte> payload)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ThrowIfDisposed();

        if (!BinaryFileTransferProtocol.HasMagic(payload.Span))
        {
            return false;
        }

        if (!BinaryFileTransferProtocol.TryParseFrame(payload, out var frame, out var error))
        {
            throw new InvalidOperationException(error);
        }

        ActiveBinaryFileDownload? activeDownload;
        lock (gate)
        {
            if (!activeByTransfer.TryGetValue(new TransferKey(sessionId.Trim(), frame.Header.TransferId), out activeDownload))
            {
                throw new InvalidOperationException(
                    $"Received ASFT frame for unknown transfer '{frame.Header.TransferId}' on session '{sessionId}'.");
            }
        }

        HandleBinaryFrame(activeDownload!, frame);
        return true;
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        List<BinaryFileDownloadOperation> pendingOperations;
        List<ActiveBinaryFileDownload> activeDownloads;
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            pendingOperations = pendingByRequestId.Values.ToList();
            activeDownloads = activeByTransfer.Values.ToList();
            pendingByRequestId.Clear();
            activeByTransfer.Clear();
        }

        foreach (var operation in pendingOperations)
        {
            operation.TrySetException(new ObjectDisposedException(nameof(BinaryFileDownloadManager)));
        }

        foreach (var activeDownload in activeDownloads)
        {
            FailActiveDownload(activeDownload, new ObjectDisposedException(nameof(BinaryFileDownloadManager)));
        }
    }

    private bool HandleToolErrorEnvelope(BinaryFileDownloadOperation operation, ToolProtocolEnvelope envelope)
    {
        lock (gate)
        {
            _ = pendingByRequestId.Remove(operation.RequestId);
        }

        var error = TryReadErrorPayload(envelope.Payload as JsonObject);
        operation.TrySetException(new InvalidOperationException(
            string.IsNullOrWhiteSpace(error.Code)
                ? error.Message
                : $"{error.Code}: {error.Message}"));
        return true;
    }

    private bool HandleToolResultEnvelope(BinaryFileDownloadOperation operation, ToolProtocolEnvelope envelope)
    {
        try
        {
            if (envelope.Payload is not JsonObject payload)
            {
                throw new InvalidOperationException("Binary download result payload must be a JSON object.");
            }

            var toolId = ReadRequiredString(payload, "toolId");
            if (!string.Equals(toolId, BeginBinaryDownloadToolId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Expected tool result for '{BeginBinaryDownloadToolId}' but received '{toolId}'.");
            }

            var resultPayload = payload["result"] as JsonObject
                ?? throw new InvalidOperationException("Binary download tool result must contain a 'result' object.");

            var transferId = ReadRequiredString(resultPayload, "transferId");
            var remoteFileName = ReadRequiredString(resultPayload, "fileName");
            var remoteFileExtension = ReadOptionalString(resultPayload, "fileExtension");
            var mimeType = ReadRequiredString(resultPayload, "mimeType");
            var version = ReadRequiredString(resultPayload, "version");
            var downloadId = ReadRequiredString(resultPayload, "downloadId");
            var sizeBytes = ReadRequiredInt64(resultPayload, "sizeBytes");
            _ = ReadRequiredString(resultPayload, "wireProtocol");
            _ = ReadRequiredString(resultPayload, "deliveryMode");

            Directory.CreateDirectory(operation.DestinationDirectoryPath);
            var destinationPath = AllocateDestinationPath(operation.DestinationDirectoryPath, remoteFileName, downloadId);
            var stream = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            var activeDownload = new ActiveBinaryFileDownload(
                operation,
                transferId,
                downloadId,
                remoteFileName,
                remoteFileExtension,
                mimeType,
                sizeBytes,
                version,
                destinationPath,
                stream);

            lock (gate)
            {
                _ = pendingByRequestId.Remove(operation.RequestId);
                activeByTransfer[new TransferKey(operation.SessionId, transferId)] = activeDownload;
            }

            return true;
        }
        catch (Exception exception)
        {
            lock (gate)
            {
                _ = pendingByRequestId.Remove(operation.RequestId);
            }

            operation.TrySetException(exception);
            return true;
        }
    }

    private void HandleBinaryFrame(ActiveBinaryFileDownload activeDownload, BinaryFileTransferFrame frame)
    {
        if (frame.Header.Sequence != activeDownload.ExpectedSequence)
        {
            FailActiveDownload(activeDownload, new InvalidOperationException(
                $"Expected ASFT sequence {activeDownload.ExpectedSequence} but received {frame.Header.Sequence}."));
            return;
        }

        if (frame.Header.OffsetBytes != activeDownload.BytesWritten)
        {
            FailActiveDownload(activeDownload, new InvalidOperationException(
                $"Expected ASFT offset {activeDownload.BytesWritten} but received {frame.Header.OffsetBytes}."));
            return;
        }

        try
        {
            switch (frame.Header.FrameType)
            {
                case BinaryFileTransferFrameType.Chunk:
                    activeDownload.Stream.Write(frame.Payload.Span);
                    activeDownload.BytesWritten += frame.Payload.Length;
                    activeDownload.ExpectedSequence++;
                    break;

                case BinaryFileTransferFrameType.Complete:
                    if (frame.Payload.Length != 0)
                    {
                        throw new InvalidOperationException("ASFT complete frames must not include payload bytes.");
                    }

                    if (activeDownload.BytesWritten != activeDownload.SizeBytes)
                    {
                        throw new InvalidOperationException(
                            $"ASFT transfer '{activeDownload.TransferId}' completed with {activeDownload.BytesWritten} bytes written, expected {activeDownload.SizeBytes}.");
                    }

                    CompleteActiveDownload(activeDownload);
                    break;

                case BinaryFileTransferFrameType.Error:
                    var message = frame.Payload.IsEmpty
                        ? "The app reported a binary transfer failure."
                        : Encoding.UTF8.GetString(frame.Payload.Span);
                    throw new InvalidOperationException(message);

                default:
                    throw new InvalidOperationException($"Unsupported ASFT frame type '{frame.Header.FrameType}'.");
            }
        }
        catch (Exception exception)
        {
            FailActiveDownload(activeDownload, exception);
        }
    }

    private void CompleteActiveDownload(ActiveBinaryFileDownload activeDownload)
    {
        lock (gate)
        {
            _ = activeByTransfer.Remove(new TransferKey(activeDownload.Operation.SessionId, activeDownload.TransferId));
        }

        activeDownload.Stream.Dispose();
        activeDownload.Operation.TrySetResult(new BinaryFileDownloadResult(
            activeDownload.Operation.SessionId,
            activeDownload.Operation.RequestId,
            activeDownload.DownloadId,
            activeDownload.TransferId,
            activeDownload.RemoteFileName,
            activeDownload.RemoteFileExtension,
            activeDownload.MimeType,
            activeDownload.SizeBytes,
            activeDownload.Version,
            activeDownload.LocalFilePath,
            DateTimeOffset.UtcNow));
    }

    private void FailActiveDownload(ActiveBinaryFileDownload activeDownload, Exception exception)
    {
        lock (gate)
        {
            _ = activeByTransfer.Remove(new TransferKey(activeDownload.Operation.SessionId, activeDownload.TransferId));
        }

        try
        {
            activeDownload.Stream.Dispose();
        }
        catch (Exception suppressedException)
        {
            System.Diagnostics.Trace.TraceWarning(suppressedException.ToString());
        }

        try
        {
            if (File.Exists(activeDownload.LocalFilePath))
            {
                File.Delete(activeDownload.LocalFilePath);
            }
        }
        catch (Exception suppressedException)
        {
            System.Diagnostics.Trace.TraceWarning(suppressedException.ToString());
        }

        activeDownload.Operation.TrySetException(exception);
    }

    private static string AllocateDestinationPath(string destinationDirectoryPath, string remoteFileName, string downloadId)
    {
        var safeFileName = SanitizeFileName(remoteFileName);
        if (string.IsNullOrWhiteSpace(safeFileName))
        {
            safeFileName = downloadId;
        }

        var candidatePath = Path.Combine(destinationDirectoryPath, safeFileName);
        if (!File.Exists(candidatePath))
        {
            return candidatePath;
        }

        var baseName = Path.GetFileNameWithoutExtension(safeFileName);
        var extension = Path.GetExtension(safeFileName);
        for (var attempt = 1; attempt < 1000; attempt++)
        {
            candidatePath = Path.Combine(destinationDirectoryPath, $"{baseName}-{attempt}{extension}");
            if (!File.Exists(candidatePath))
            {
                return candidatePath;
            }
        }

        return Path.Combine(destinationDirectoryPath, $"{baseName}-{downloadId}{extension}");
    }

    private static string SanitizeFileName(string fileName)
    {
        var safeChars = fileName
            .Trim()
            .Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch)
            .ToArray();

        return new string(safeChars);
    }

    private static BinaryDownloadErrorResult TryReadErrorPayload(JsonObject? payload)
    {
        if (payload is null)
        {
            return new BinaryDownloadErrorResult(null, "Binary download request failed.");
        }

        return new BinaryDownloadErrorResult(
            ReadOptionalString(payload, "code"),
            ReadOptionalString(payload, "message") ?? "Binary download request failed.");
    }

    private static string ReadRequiredString(JsonObject payload, string propertyName)
    {
        var value = ReadOptionalString(payload, propertyName);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"Binary download payload is missing '{propertyName}'.");
        }

        return value;
    }

    private static string? ReadOptionalString(JsonObject payload, string propertyName)
        => payload[propertyName]?.GetValue<string>();

    private static long ReadRequiredInt64(JsonObject payload, string propertyName)
    {
        var node = payload[propertyName];
        if (node is null)
        {
            throw new InvalidOperationException($"Binary download payload is missing '{propertyName}'.");
        }

        if (node is JsonValue value)
        {
            if (value.TryGetValue<long>(out var longValue))
            {
                return longValue;
            }

            if (value.TryGetValue<int>(out var intValue))
            {
                return intValue;
            }

            if (long.TryParse(value.ToString(), out var parsedValue))
            {
                return parsedValue;
            }
        }

        throw new InvalidOperationException($"Binary download payload '{propertyName}' must be an integer.");
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
    }

    private readonly record struct TransferKey(string SessionId, string TransferId);

    private sealed class ActiveBinaryFileDownload
    {
        internal ActiveBinaryFileDownload(
            BinaryFileDownloadOperation operation,
            string transferId,
            string downloadId,
            string remoteFileName,
            string? remoteFileExtension,
            string mimeType,
            long sizeBytes,
            string version,
            string localFilePath,
            FileStream stream)
        {
            Operation = operation;
            TransferId = transferId;
            DownloadId = downloadId;
            RemoteFileName = remoteFileName;
            RemoteFileExtension = remoteFileExtension;
            MimeType = mimeType;
            SizeBytes = sizeBytes;
            Version = version;
            LocalFilePath = localFilePath;
            Stream = stream;
        }

        internal BinaryFileDownloadOperation Operation { get; }

        internal string TransferId { get; }

        internal string DownloadId { get; }

        internal string RemoteFileName { get; }

        internal string? RemoteFileExtension { get; }

        internal string MimeType { get; }

        internal long SizeBytes { get; }

        internal string Version { get; }

        internal string LocalFilePath { get; }

        internal FileStream Stream { get; }

        internal int ExpectedSequence { get; set; }

        internal long BytesWritten { get; set; }
    }
}
