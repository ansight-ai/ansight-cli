using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace Ansight.Host.Runtime.Feedback;

internal sealed class AnnotatedFeedbackTransferManager : IDisposable
{
    public const string SubmitAction = "annotation.submit";
    public const string SubmitSchema = "ansight.annotation.submit.v1";

    private readonly Lock gate = new();
    private readonly Dictionary<TransferKey, ActiveTransfer> activeTransfers = new();
    private bool disposed;

    public bool TryRegister(
        string sessionId,
        JsonObject? payload,
        out string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ThrowIfDisposed();

        if (!TryCreateDescriptor(payload, out var descriptor, out message) || descriptor is null)
        {
            return false;
        }

        var directoryPath = Path.Combine(
            Path.GetTempPath(),
            "AnsightHost",
            "annotated-feedback",
            SanitizePathSegment(sessionId));
        Directory.CreateDirectory(directoryPath);
        var bundlePath = Path.Combine(directoryPath, $"{descriptor.TransferId}.ansightannotation.part");

        try
        {
            var stream = new FileStream(bundlePath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            var activeTransfer = new ActiveTransfer(sessionId.Trim(), descriptor, bundlePath, stream);
            lock (gate)
            {
                var key = new TransferKey(activeTransfer.SessionId, activeTransfer.TransferId);
                if (activeTransfers.ContainsKey(key))
                {
                    stream.Dispose();
                    File.Delete(bundlePath);
                    message = $"Annotated feedback transfer '{descriptor.TransferId}' is already active.";
                    return false;
                }

                activeTransfers[key] = activeTransfer;
            }

            message = "Annotated feedback transfer accepted.";
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            TryDeleteFile(bundlePath);
            message = $"Could not prepare annotated feedback transfer: {exception.Message}";
            return false;
        }
    }

    public bool TryHandleBinaryMessage(
        string sessionId,
        ReadOnlyMemory<byte> payload,
        out AnnotatedFeedbackTransferCompletion? completion)
    {
        completion = null;
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

        ActiveTransfer? activeTransfer;
        var key = new TransferKey(sessionId.Trim(), frame.Header.TransferId);
        lock (gate)
        {
            activeTransfers.TryGetValue(key, out activeTransfer);
        }

        if (activeTransfer is null)
        {
            return false;
        }

        completion = HandleFrame(activeTransfer, frame);
        return true;
    }

    public void CancelSession(string sessionId, string reason)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        ActiveTransfer[] transfers;
        lock (gate)
        {
            transfers = activeTransfers.Values
                .Where(transfer => string.Equals(transfer.SessionId, sessionId.Trim(), StringComparison.Ordinal))
                .ToArray();
        }

        foreach (var transfer in transfers)
        {
            FailTransfer(transfer, new IOException(reason));
        }
    }

    public void Dispose()
    {
        ActiveTransfer[] transfers;
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            transfers = activeTransfers.Values.ToArray();
            activeTransfers.Clear();
        }

        foreach (var transfer in transfers)
        {
            DisposeAndDelete(transfer);
        }
    }

    private AnnotatedFeedbackTransferCompletion? HandleFrame(
        ActiveTransfer activeTransfer,
        BinaryFileTransferFrame frame)
    {
        if (frame.Header.Sequence != activeTransfer.ExpectedSequence)
        {
            FailTransfer(activeTransfer, new InvalidOperationException(
                $"Expected annotated feedback ASFT sequence {activeTransfer.ExpectedSequence} but received {frame.Header.Sequence}."));
            return null;
        }

        if (frame.Header.OffsetBytes != activeTransfer.BytesWritten)
        {
            FailTransfer(activeTransfer, new InvalidOperationException(
                $"Expected annotated feedback ASFT offset {activeTransfer.BytesWritten} but received {frame.Header.OffsetBytes}."));
            return null;
        }

        try
        {
            switch (frame.Header.FrameType)
            {
                case BinaryFileTransferFrameType.Chunk:
                    if (activeTransfer.BytesWritten + frame.Payload.Length > activeTransfer.SizeBytes)
                    {
                        throw new InvalidDataException("Annotated feedback transfer exceeded its declared size.");
                    }

                    activeTransfer.Stream.Write(frame.Payload.Span);
                    activeTransfer.BytesWritten += frame.Payload.Length;
                    activeTransfer.ExpectedSequence++;
                    return null;
                case BinaryFileTransferFrameType.Complete:
                    if (!frame.Payload.IsEmpty)
                    {
                        throw new InvalidDataException("Annotated feedback ASFT complete frames must not include payload bytes.");
                    }

                    if (activeTransfer.BytesWritten != activeTransfer.SizeBytes)
                    {
                        throw new InvalidDataException(
                            $"Annotated feedback transfer completed with {activeTransfer.BytesWritten} bytes, expected {activeTransfer.SizeBytes}.");
                    }

                    lock (gate)
                    {
                        activeTransfers.Remove(new TransferKey(activeTransfer.SessionId, activeTransfer.TransferId));
                    }

                    activeTransfer.Stream.Flush(flushToDisk: true);
                    activeTransfer.Stream.Dispose();
                    return new AnnotatedFeedbackTransferCompletion(
                        activeTransfer.SessionId,
                        activeTransfer.TransferId,
                        activeTransfer.ClientAnnotationId,
                        activeTransfer.CapturedAtUtc,
                        activeTransfer.BundlePath);
                case BinaryFileTransferFrameType.Error:
                    throw new InvalidDataException(frame.Payload.IsEmpty
                        ? "The app reported an annotated feedback transfer failure."
                        : Encoding.UTF8.GetString(frame.Payload.Span));
                default:
                    throw new InvalidDataException($"Unsupported ASFT frame type '{frame.Header.FrameType}'.");
            }
        }
        catch (Exception exception)
        {
            FailTransfer(activeTransfer, exception);
            return null;
        }
    }

    private void FailTransfer(ActiveTransfer activeTransfer, Exception exception)
    {
        lock (gate)
        {
            activeTransfers.Remove(new TransferKey(activeTransfer.SessionId, activeTransfer.TransferId));
        }

        DisposeAndDelete(activeTransfer);
    }

    private static void DisposeAndDelete(ActiveTransfer activeTransfer)
    {
        try
        {
            activeTransfer.Stream.Dispose();
        }
        catch (Exception suppressedException)
        {
            System.Diagnostics.Trace.TraceWarning(suppressedException.ToString());
        }

        TryDeleteFile(activeTransfer.BundlePath);
    }

    private static bool TryCreateDescriptor(
        JsonObject? payload,
        out TransferDescriptor? descriptor,
        out string message)
    {
        descriptor = null;
        if (payload is null || !string.Equals(ReadString(payload, "schema"), SubmitSchema, StringComparison.Ordinal))
        {
            message = "Annotated feedback submission is missing a supported schema.";
            return false;
        }

        var clientAnnotationId = NormalizeGuid(ReadString(payload, "clientAnnotationId"));
        if (clientAnnotationId is null)
        {
            message = "Annotated feedback submission is missing a valid clientAnnotationId.";
            return false;
        }

        if (!DateTimeOffset.TryParse(
                ReadString(payload, "capturedAtUtc"),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var capturedAtUtc))
        {
            message = "Annotated feedback submission is missing a valid capturedAtUtc timestamp.";
            return false;
        }

        if (payload["transfer"] is not JsonObject transfer)
        {
            message = "Annotated feedback submission is missing its binary transfer descriptor.";
            return false;
        }

        var transferId = NormalizeGuid(ReadString(transfer, "transferId"));
        var sizeBytes = ReadLong(transfer, "sizeBytes");
        if (transferId is null || sizeBytes is null || sizeBytes < 0 || sizeBytes > AnnotatedFeedbackBundleReader.MaximumBundleBytes)
        {
            message = "Annotated feedback transfer id or size is invalid.";
            return false;
        }

        if (!string.Equals(ReadString(transfer, "wireProtocol"), BinaryFileTransferProtocol.ProtocolName, StringComparison.Ordinal)
            || !string.Equals(ReadString(transfer, "mimeType"), AnnotatedFeedbackBundleReader.BundleMimeType, StringComparison.OrdinalIgnoreCase))
        {
            message = "Annotated feedback transfer protocol or MIME type is unsupported.";
            return false;
        }

        descriptor = new TransferDescriptor(transferId, clientAnnotationId, capturedAtUtc, sizeBytes.Value);
        message = string.Empty;
        return true;
    }

    private static string? ReadString(JsonObject payload, string propertyName)
        => payload[propertyName] is JsonValue value && value.TryGetValue<string>(out var result) ? result : null;

    private static long? ReadLong(JsonObject payload, string propertyName)
    {
        if (payload[propertyName] is not JsonValue value)
        {
            return null;
        }

        return value.TryGetValue<long>(out var longValue)
            ? longValue
            : value.TryGetValue<int>(out var intValue)
                ? intValue
                : null;
    }

    private static string? NormalizeGuid(string? value)
        => Guid.TryParse(value, out var parsed) ? parsed.ToString("N") : null;

    private static string SanitizePathSegment(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var result = new string(value.Trim().Select(character => invalid.Contains(character) ? '_' : character).ToArray());
        return string.IsNullOrWhiteSpace(result) ? "unknown-session" : result;
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception suppressedException)
        {
            System.Diagnostics.Trace.TraceWarning(suppressedException.ToString());
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(disposed, this);

    private readonly record struct TransferKey(string SessionId, string TransferId);

    private sealed record TransferDescriptor(
        string TransferId,
        string ClientAnnotationId,
        DateTimeOffset CapturedAtUtc,
        long SizeBytes);

    private sealed class ActiveTransfer
    {
        public ActiveTransfer(string sessionId, TransferDescriptor descriptor, string bundlePath, FileStream stream)
        {
            SessionId = sessionId;
            TransferId = descriptor.TransferId;
            ClientAnnotationId = descriptor.ClientAnnotationId;
            CapturedAtUtc = descriptor.CapturedAtUtc;
            SizeBytes = descriptor.SizeBytes;
            BundlePath = bundlePath;
            Stream = stream;
        }

        public string SessionId { get; }
        public string TransferId { get; }
        public string ClientAnnotationId { get; }
        public DateTimeOffset CapturedAtUtc { get; }
        public long SizeBytes { get; }
        public string BundlePath { get; }
        public FileStream Stream { get; }
        public int ExpectedSequence { get; set; }
        public long BytesWritten { get; set; }
    }
}
