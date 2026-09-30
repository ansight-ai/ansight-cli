using System.Text;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Ansight.Tools;

namespace Ansight.Host.Runtime.BinaryTransfers;

internal sealed class BinaryToolArtifactTransferManager : IDisposable
{
    private const string ArtifactRequestToolId = "artifacts.request";
    private const string FilesBeginBinaryDownloadToolId = "files.begin_binary_download";
    private const string UiGetScreenshotToolId = "ui.get_screenshot";
    private readonly Lock gate = new();
    private readonly Dictionary<TransferKey, ActiveTransfer> activeByTransfer = new();
    // A fast transfer can finish before its response caller begins awaiting it. Keep the
    // completion with that response, independently of the active stream's lifetime.
    private readonly ConditionalWeakTable<ToolProtocolEnvelope, ResponseTransfers> completionsByResponse = new();
    private bool disposed;

    public BinaryToolArtifactTransferRegistration? TryRegister(string sessionId, string appId, ToolProtocolEnvelope envelope)
        => TryRegisterAll(sessionId, appId, envelope).FirstOrDefault();

    public IReadOnlyList<BinaryToolArtifactTransferRegistration> TryRegisterAll(
        string sessionId,
        string appId,
        ToolProtocolEnvelope envelope)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        ArgumentNullException.ThrowIfNull(envelope);
        ThrowIfDisposed();

        var descriptors = new List<BinaryToolArtifactTransferDescriptor>();
        CollectDescriptors(envelope.Payload, inheritedToolId: null, descriptors);
        if (descriptors.Count == 0)
        {
            return [];
        }

        lock (gate)
        {
            ThrowIfDisposed();
            if (completionsByResponse.TryGetValue(envelope, out var existing))
            {
                return existing.Registrations;
            }

            var registrations = descriptors.Select(descriptor => Register(sessionId, appId, descriptor)).ToArray();
            completionsByResponse.Add(envelope, new ResponseTransfers(sessionId.Trim(), registrations));
            return registrations;
        }
    }

    private BinaryToolArtifactTransferRegistration Register(
        string sessionId,
        string appId,
        BinaryToolArtifactTransferDescriptor descriptor)
    {
        var destinationPath = AllocateArtifactPath(appId, sessionId, descriptor.FileName);
        var stream = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        var activeTransfer = new ActiveTransfer(
            sessionId.Trim(),
            descriptor,
            destinationPath,
            stream);

        descriptor.ResultPayload["artifactPath"] = destinationPath;
        descriptor.ResultPayload["artifactKind"] = descriptor.Kind;
        descriptor.ResultPayload["mimeType"] = descriptor.MimeType;
        descriptor.ResultPayload["status"] = "receiving";

        lock (gate)
        {
            activeByTransfer[new TransferKey(activeTransfer.SessionId, descriptor.TransferId)] = activeTransfer;
        }

        return CreateRegistration(activeTransfer);
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

        ActiveTransfer? activeTransfer;
        var key = new TransferKey(sessionId.Trim(), frame.Header.TransferId);
        lock (gate)
        {
            activeByTransfer.TryGetValue(key, out activeTransfer);
        }

        if (activeTransfer is null)
        {
            return false;
        }

        HandleFrame(activeTransfer, frame);
        return true;
    }

    public bool TryGetCompletion(string sessionId, ToolProtocolEnvelope envelope, out BinaryToolArtifactTransferRegistration registration)
    {
        registration = TryGetCompletions(sessionId, envelope).FirstOrDefault()!;
        return registration is not null;
    }

    public IReadOnlyList<BinaryToolArtifactTransferRegistration> TryGetCompletions(
        string sessionId,
        ToolProtocolEnvelope envelope)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(envelope);
        lock (gate)
        {
            if (completionsByResponse.TryGetValue(envelope, out var responseTransfers)
                && string.Equals(responseTransfers.SessionId, sessionId.Trim(), StringComparison.Ordinal))
            {
                // One consumer owns persistence, including when the transfer already completed.
                completionsByResponse.Remove(envelope);
                return responseTransfers.Registrations;
            }
        }
        return [];
    }

    public void CancelResponse(string sessionId, ToolProtocolEnvelope envelope, string reason)
    {
        var registrations = TryGetCompletions(sessionId, envelope);
        foreach (var registration in registrations)
        {
            FailTransfer(sessionId, registration.TransferId, new OperationCanceledException(reason));
        }
    }

    public void CancelSession(string sessionId, string reason)
    {
        List<ActiveTransfer> activeTransfers;
        lock (gate)
        {
            foreach (var response in completionsByResponse
                         .Where(candidate => string.Equals(candidate.Value.SessionId, sessionId.Trim(), StringComparison.Ordinal))
                         .Select(candidate => candidate.Key).ToArray())
            {
                completionsByResponse.Remove(response);
            }
            activeTransfers = activeByTransfer.Values
                .Where(candidate => string.Equals(candidate.SessionId, sessionId.Trim(), StringComparison.Ordinal)).ToList();
        }
        foreach (var transfer in activeTransfers)
        {
            FailActiveTransfer(transfer, new OperationCanceledException(reason));
        }
    }

    public void FailTransfer(string sessionId, string transferId, Exception exception)
    {
        ActiveTransfer? activeTransfer;
        lock (gate)
        {
            activeByTransfer.TryGetValue(new TransferKey(sessionId.Trim(), transferId), out activeTransfer);
        }

        if (activeTransfer is not null)
        {
            FailActiveTransfer(activeTransfer, exception);
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        List<ActiveTransfer> activeTransfers;
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            activeTransfers = activeByTransfer.Values.ToList();
            activeByTransfer.Clear();
            completionsByResponse.Clear();
        }

        foreach (var activeTransfer in activeTransfers)
        {
            FailActiveTransfer(activeTransfer, new ObjectDisposedException(nameof(BinaryToolArtifactTransferManager)));
        }
    }

    private void HandleFrame(ActiveTransfer activeTransfer, BinaryFileTransferFrame frame)
    {
        if (frame.Header.Sequence != activeTransfer.ExpectedSequence)
        {
            FailActiveTransfer(activeTransfer, new InvalidOperationException(
                $"Expected ASFT sequence {activeTransfer.ExpectedSequence} but received {frame.Header.Sequence}."));
            return;
        }

        if (frame.Header.OffsetBytes != activeTransfer.BytesWritten)
        {
            FailActiveTransfer(activeTransfer, new InvalidOperationException(
                $"Expected ASFT offset {activeTransfer.BytesWritten} but received {frame.Header.OffsetBytes}."));
            return;
        }

        try
        {
            switch (frame.Header.FrameType)
            {
                case BinaryFileTransferFrameType.Chunk:
                    activeTransfer.Stream.Write(frame.Payload.Span);
                    activeTransfer.BytesWritten += frame.Payload.Length;
                    activeTransfer.ExpectedSequence++;
                    activeTransfer.ResultPayload["receivedBytes"] = activeTransfer.BytesWritten;
                    break;
                case BinaryFileTransferFrameType.Complete:
                    CompleteActiveTransfer(activeTransfer, frame);
                    break;
                case BinaryFileTransferFrameType.Error:
                    var message = frame.Payload.IsEmpty
                        ? "The app reported a binary artifact transfer failure."
                        : Encoding.UTF8.GetString(frame.Payload.Span);
                    throw new InvalidOperationException(message);
                default:
                    throw new InvalidOperationException($"Unsupported ASFT frame type '{frame.Header.FrameType}'.");
            }
        }
        catch (Exception exception)
        {
            FailActiveTransfer(activeTransfer, exception);
        }
    }

    private void CompleteActiveTransfer(ActiveTransfer activeTransfer, BinaryFileTransferFrame frame)
    {
        if (frame.Payload.Length != 0)
        {
            throw new InvalidOperationException("ASFT complete frames must not include payload bytes.");
        }

        if (activeTransfer.SizeBytes is { } sizeBytes && activeTransfer.BytesWritten != sizeBytes)
        {
            throw new InvalidOperationException(
                $"ASFT transfer '{activeTransfer.TransferId}' completed with {activeTransfer.BytesWritten} bytes written, expected {sizeBytes}.");
        }

        lock (gate)
        {
            _ = activeByTransfer.Remove(new TransferKey(activeTransfer.SessionId, activeTransfer.TransferId));
        }

        activeTransfer.Stream.Dispose();
        activeTransfer.ResultPayload["status"] = "complete";
        activeTransfer.ResultPayload["receivedBytes"] = activeTransfer.BytesWritten;
        activeTransfer.Completion.TrySetResult();
    }

    private void FailActiveTransfer(ActiveTransfer activeTransfer, Exception exception)
    {
        lock (gate)
        {
            _ = activeByTransfer.Remove(new TransferKey(activeTransfer.SessionId, activeTransfer.TransferId));
        }

        try
        {
            activeTransfer.Stream.Dispose();
        }
        catch (Exception suppressedException)
        {
            System.Diagnostics.Trace.TraceWarning(suppressedException.ToString());
        }

        try
        {
            if (File.Exists(activeTransfer.ArtifactPath))
            {
                File.Delete(activeTransfer.ArtifactPath);
            }
        }
        catch (Exception suppressedException)
        {
            System.Diagnostics.Trace.TraceWarning(suppressedException.ToString());
        }

        activeTransfer.ResultPayload["status"] = "failed";
        activeTransfer.ResultPayload["message"] = exception.Message;
        activeTransfer.Completion.TrySetException(exception);
    }

    private static BinaryToolArtifactTransferRegistration CreateRegistration(ActiveTransfer activeTransfer)
    {
        return new BinaryToolArtifactTransferRegistration(
            activeTransfer.SessionId,
            activeTransfer.TransferId,
            activeTransfer.ArtifactPath,
            activeTransfer.ToolId,
            activeTransfer.Name,
            activeTransfer.Kind,
            activeTransfer.MimeType,
            activeTransfer.FileName,
            activeTransfer.ProviderId,
            activeTransfer.ArtifactId,
            activeTransfer.CapturedAtUtc,
            activeTransfer.CaptureSessionArtifactSnapshot,
            activeTransfer.Completion.Task);
    }

    private static bool TryCreateDescriptor(
        ToolProtocolEnvelope envelope,
        out BinaryToolArtifactTransferDescriptor? descriptor)
    {
        descriptor = null;
        if (envelope.Payload is not JsonObject payload ||
            payload["result"] is not JsonObject result)
        {
            return false;
        }
        return TryCreateDescriptor(ReadOptionalString(payload, "toolId"), result, out descriptor);
    }

    private static bool TryCreateDescriptor(
        string? toolId,
        JsonObject result,
        out BinaryToolArtifactTransferDescriptor? descriptor)
    {
        descriptor = null;
        if (
            !string.Equals(ReadOptionalString(result, "deliveryMode"), "websocket_binary", StringComparison.Ordinal) ||
            !string.Equals(ReadOptionalString(result, "wireProtocol"), BinaryFileTransferProtocol.ProtocolName, StringComparison.Ordinal))
        {
            return false;
        }

        if (string.Equals(toolId, UiGetScreenshotToolId, StringComparison.Ordinal))
        {
            descriptor = CreateScreenshotDescriptor(result);
            return true;
        }

        if (string.Equals(toolId, ArtifactRequestToolId, StringComparison.Ordinal))
        {
            descriptor = CreateArtifactRequestDescriptor(result);
            return true;
        }

        if (string.Equals(toolId, FilesBeginBinaryDownloadToolId, StringComparison.Ordinal))
        {
            descriptor = CreateFileDownloadDescriptor(result);
            return true;
        }

        return false;
    }

    private static void CollectDescriptors(
        JsonNode? node,
        string? inheritedToolId,
        List<BinaryToolArtifactTransferDescriptor> descriptors)
    {
        if (node is JsonArray array)
        {
            foreach (var item in array)
            {
                CollectDescriptors(item, inheritedToolId: null, descriptors);
            }
            return;
        }

        if (node is not JsonObject payload)
        {
            return;
        }

        var toolId = ReadOptionalString(payload, "toolId") ?? inheritedToolId;
        if (payload["result"] is JsonObject result
            && TryCreateDescriptor(toolId, result, out var descriptor)
            && descriptor is not null)
        {
            descriptors.Add(descriptor);
        }

        foreach (var property in payload)
        {
            if (!string.Equals(property.Key, "result", StringComparison.Ordinal))
            {
                CollectDescriptors(property.Value, toolId, descriptors);
            }
        }
    }

    private static void CollectTransferIds(JsonNode? node, HashSet<string> transferIds)
    {
        if (node is JsonArray array)
        {
            foreach (var item in array)
            {
                CollectTransferIds(item, transferIds);
            }
            return;
        }
        if (node is not JsonObject payload)
        {
            return;
        }

        if (payload["result"] is JsonObject result
            && string.Equals(ReadOptionalString(result, "deliveryMode"), "websocket_binary", StringComparison.Ordinal)
            && ReadOptionalString(result, "transferId") is { Length: > 0 } transferId)
        {
            transferIds.Add(transferId);
        }
        foreach (var property in payload)
        {
            if (!string.Equals(property.Key, "result", StringComparison.Ordinal))
            {
                CollectTransferIds(property.Value, transferIds);
            }
        }
    }

    private static BinaryToolArtifactTransferDescriptor CreateScreenshotDescriptor(JsonObject result)
    {
        var transferId = ReadRequiredString(result, "transferId");
        var sizeBytes = ReadRequiredInt64(result, "sizeBytes");
        var format = ReadOptionalString(result, "format") ?? "jpeg";
        var mimeType = ReadOptionalString(result, "mimeType") ?? ResolveMimeType(format);
        var fileName = ReadOptionalString(result, "fileName") ?? $"screenshot-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmssfff}.{ResolveExtension(format)}";
        return new BinaryToolArtifactTransferDescriptor(
            ToolId: UiGetScreenshotToolId,
            TransferId: transferId,
            SizeBytes: sizeBytes,
            FileName: fileName,
            Name: Path.GetFileNameWithoutExtension(fileName),
            Kind: "screenshot",
            MimeType: mimeType,
            ProviderId: null,
            ArtifactId: null,
            CapturedAtUtc: DateTimeOffset.UtcNow,
            CaptureSessionArtifactSnapshot: false,
            ResultPayload: result);
    }

    private static BinaryToolArtifactTransferDescriptor CreateFileDownloadDescriptor(JsonObject result)
    {
        var transferId = ReadRequiredString(result, "transferId");
        var sizeBytes = ReadRequiredInt64(result, "sizeBytes");
        if (sizeBytes < 0)
        {
            throw new InvalidOperationException("Binary file download payload 'sizeBytes' must be a non-negative integer.");
        }

        var fallbackFileName = $"download-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmssfff}.bin";
        var fileName = FirstNonEmpty(
            ReadOptionalString(result, "fileName"),
            Path.GetFileName(ReadOptionalString(result, "relativePath")),
            fallbackFileName);
        var name = FirstNonEmpty(
            Path.GetFileNameWithoutExtension(fileName),
            fileName);
        var mimeType = FirstNonEmpty(
            ReadOptionalString(result, "mimeType"),
            "application/octet-stream");
        var artifactId = FirstNonEmpty(
            ReadOptionalString(result, "relativePath"),
            fileName);
        var capturedAtUtc = FirstDate(
            ReadOptionalString(result, "capturedAtUtc"),
            second: null,
            DateTimeOffset.UtcNow);

        return new BinaryToolArtifactTransferDescriptor(
            ToolId: FilesBeginBinaryDownloadToolId,
            TransferId: transferId,
            SizeBytes: sizeBytes,
            FileName: fileName,
            Name: name,
            Kind: "file",
            MimeType: mimeType,
            ProviderId: "files",
            ArtifactId: artifactId,
            CapturedAtUtc: capturedAtUtc,
            CaptureSessionArtifactSnapshot: true,
            ResultPayload: result);
    }

    private static BinaryToolArtifactTransferDescriptor CreateArtifactRequestDescriptor(JsonObject result)
    {
        var artifact = result["artifact"] as JsonObject;
        var transferId = ReadRequiredString(result, "transferId");
        var sizeBytes = ReadOptionalInt64(artifact, "sizeBytes") ?? ReadOptionalInt64(result, "sizeBytes");
        if (sizeBytes < 0)
        {
            throw new InvalidOperationException("Binary artifact payload 'sizeBytes' must be a non-negative integer.");
        }

        var fallbackFileName = $"artifact-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmssfff}.bin";
        var fileName = FirstNonEmpty(
            ReadOptionalString(artifact, "fileName"),
            ReadOptionalString(result, "fileName"),
            fallbackFileName);
        var name = FirstNonEmpty(
            ReadOptionalString(artifact, "name"),
            Path.GetFileNameWithoutExtension(fileName),
            fileName);
        var kind = FirstNonEmpty(
            ReadOptionalString(artifact, "kind"),
            "artifact");
        var mimeType = FirstNonEmpty(
            ReadOptionalString(artifact, "mimeType"),
            ReadOptionalString(result, "mimeType"),
            "application/octet-stream");
        var capturedAtUtc = FirstDate(
            ReadOptionalString(artifact, "createdAtUtc"),
            ReadOptionalString(result, "capturedAtUtc"),
            DateTimeOffset.UtcNow);

        return new BinaryToolArtifactTransferDescriptor(
            ToolId: ArtifactRequestToolId,
            TransferId: transferId,
            SizeBytes: sizeBytes,
            FileName: fileName,
            Name: name,
            Kind: kind,
            MimeType: mimeType,
            ProviderId: ReadOptionalString(artifact, "providerId"),
            ArtifactId: ReadOptionalString(artifact, "artifactId"),
            CapturedAtUtc: capturedAtUtc,
            CaptureSessionArtifactSnapshot: true,
            ResultPayload: result);
    }

    private static string AllocateArtifactPath(string appId, string sessionId, string fileName)
    {
        var artifactDirectory = Path.Combine(
            Path.GetTempPath(),
            "AnsightHost",
            "tool-artifacts",
            SanitizePathSegment(appId),
            SanitizePathSegment(sessionId));
        Directory.CreateDirectory(artifactDirectory);

        var safeFileName = SanitizeFileName(fileName);
        if (string.IsNullOrWhiteSpace(safeFileName))
        {
            safeFileName = $"screenshot-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmssfff}.jpg";
        }

        var candidatePath = Path.Combine(artifactDirectory, safeFileName);
        if (!File.Exists(candidatePath))
        {
            return candidatePath;
        }

        var baseName = Path.GetFileNameWithoutExtension(safeFileName);
        var extension = Path.GetExtension(safeFileName);
        for (var attempt = 1; attempt < 1000; attempt++)
        {
            candidatePath = Path.Combine(artifactDirectory, $"{baseName}-{attempt}{extension}");
            if (!File.Exists(candidatePath))
            {
                return candidatePath;
            }
        }

        return Path.Combine(artifactDirectory, $"{baseName}-{Guid.NewGuid():N}{extension}");
    }

    private static string SanitizePathSegment(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "unknown";
        }

        var invalidCharacters = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(value.Length);
        foreach (var character in value.Trim())
        {
            builder.Append(invalidCharacters.Contains(character) ? '_' : character);
        }

        return builder.Length == 0 ? "unknown" : builder.ToString();
    }

    private static string SanitizeFileName(string fileName)
    {
        var invalidCharacters = Path.GetInvalidFileNameChars();
        var safeChars = fileName
            .Trim()
            .Select(character => invalidCharacters.Contains(character) ? '_' : character)
            .ToArray();

        return new string(safeChars);
    }

    private static string ResolveExtension(string format)
        => string.Equals(format, "jpeg", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(format, "jpg", StringComparison.OrdinalIgnoreCase)
            ? "jpg"
            : "png";

    private static string ResolveMimeType(string format)
        => string.Equals(format, "jpeg", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(format, "jpg", StringComparison.OrdinalIgnoreCase)
            ? "image/jpeg"
            : "image/png";

    private static string ReadRequiredString(JsonObject payload, string propertyName)
    {
        var value = ReadOptionalString(payload, propertyName);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"Binary artifact payload is missing '{propertyName}'.");
        }

        return value;
    }

    private static string? ReadOptionalString(JsonObject? payload, string propertyName)
        => payload?[propertyName] is JsonValue value
           && value.TryGetValue<string>(out var result)
            ? result
            : null;

    private static long ReadRequiredInt64(JsonObject payload, string propertyName)
    {
        var parsed = ReadOptionalInt64(payload, propertyName);
        if (parsed is not null)
        {
            return parsed.Value;
        }

        throw new InvalidOperationException($"Binary artifact payload '{propertyName}' must be an integer.");
    }

    private static long? ReadOptionalInt64(JsonObject? payload, string propertyName)
    {
        var node = payload?[propertyName];
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

        return null;
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return string.Empty;
    }

    private static DateTimeOffset FirstDate(string? first, string? second, DateTimeOffset fallback)
    {
        return TryParseDate(first, out var firstDate)
            ? firstDate
            : TryParseDate(second, out var secondDate)
                ? secondDate
                : fallback.ToUniversalTime();
    }

    private static bool TryParseDate(string? value, out DateTimeOffset date)
    {
        return DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out date);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
    }

    private readonly record struct TransferKey(string SessionId, string TransferId);

    private sealed record BinaryToolArtifactTransferDescriptor(
        string ToolId,
        string TransferId,
        long? SizeBytes,
        string FileName,
        string Name,
        string Kind,
        string MimeType,
        string? ProviderId,
        string? ArtifactId,
        DateTimeOffset CapturedAtUtc,
        bool CaptureSessionArtifactSnapshot,
        JsonObject ResultPayload);

    private sealed record ResponseTransfers(
        string SessionId,
        IReadOnlyList<BinaryToolArtifactTransferRegistration> Registrations);

    private sealed class ActiveTransfer
    {
        public ActiveTransfer(
            string sessionId,
            BinaryToolArtifactTransferDescriptor descriptor,
            string artifactPath,
            FileStream stream)
        {
            ArgumentNullException.ThrowIfNull(descriptor);
            SessionId = sessionId;
            TransferId = descriptor.TransferId;
            SizeBytes = descriptor.SizeBytes;
            ArtifactPath = artifactPath;
            Stream = stream;
            ResultPayload = descriptor.ResultPayload;
            ToolId = descriptor.ToolId;
            Name = descriptor.Name;
            Kind = descriptor.Kind;
            MimeType = descriptor.MimeType;
            FileName = descriptor.FileName;
            ProviderId = descriptor.ProviderId;
            ArtifactId = descriptor.ArtifactId;
            CapturedAtUtc = descriptor.CapturedAtUtc;
            CaptureSessionArtifactSnapshot = descriptor.CaptureSessionArtifactSnapshot;
        }

        public string SessionId { get; }

        public string TransferId { get; }

        public long? SizeBytes { get; }

        public string ArtifactPath { get; }

        public FileStream Stream { get; }

        public JsonObject ResultPayload { get; }

        public string ToolId { get; }

        public string Name { get; }

        public string Kind { get; }

        public string MimeType { get; }

        public string FileName { get; }

        public string? ProviderId { get; }

        public string? ArtifactId { get; }

        public DateTimeOffset CapturedAtUtc { get; }

        public bool CaptureSessionArtifactSnapshot { get; }

        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int ExpectedSequence { get; set; }

        public long BytesWritten { get; set; }
    }
}
