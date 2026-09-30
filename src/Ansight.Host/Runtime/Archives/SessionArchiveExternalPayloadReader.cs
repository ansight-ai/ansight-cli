using System.IO.Compression;
using System.Text.Json.Nodes;
using Ansight.Pairing.Models;
using static Ansight.Host.Runtime.Archives.SessionArchiveJsonValueReader;
using AppLifecycleState = global::Ansight.AppLifecycleState;

namespace Ansight.Host.Runtime.Archives;

internal static partial class SessionArchiveExternalPayloadReader
{
    private const string ArtifactsArchiveDirectoryName = "artifacts";
    private const string SessionLogsArchiveEntryPath = "session-data/logs.json";
    private const string SessionTelemetryArchiveEntryPath = "session-data/telemetry.json";
    private const string SessionTouchesArchiveEntryPath = "session-data/touches.json";

    private static readonly JsonSerializerOptions ArchiveJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private static readonly JsonDocumentOptions JsonDocumentOptions = new()
    {
        AllowTrailingCommas = true
    };

    private static readonly string[] JsonLinesArchiveEntryPaths =
    [
        "session-data/logs.jsonl",
        "logs.jsonl",
        "session-data/events.jsonl",
        "events.jsonl",
        "session-data/metric-channels.jsonl",
        "metric-channels.jsonl",
        "session-data/metrics.jsonl",
        "metrics.jsonl",
        "session-data/telemetry.jsonl",
        "telemetry.jsonl",
        "session-data/touches.jsonl",
        "touches.jsonl",
        "session-data/touch-input.jsonl",
        "touch-input.jsonl"
    ];

    public static bool IsJsonLinesFilePath(string filePath)
    {
        var extension = Path.GetExtension(filePath);
        return string.Equals(extension, ".jsonl", StringComparison.OrdinalIgnoreCase)
               || string.Equals(extension, ".ndjson", StringComparison.OrdinalIgnoreCase);
    }

    public static bool TryReadJsonLinesFile(
        string filePath,
        out SessionArchiveJsonLinesPayload? payload,
        out string? errorMessage)
    {
        payload = null;
        errorMessage = null;

        try
        {
            var builder = new JsonLinesSessionBuilder(Path.GetFileNameWithoutExtension(filePath));
            using var stream = File.OpenRead(filePath);
            ReadJsonLines(stream, filePath, builder);

            if (!builder.HasAnyEvidence)
            {
                errorMessage = "The selected JSONL file did not contain any supported Ansight session records.";
                return false;
            }

            payload = builder.Build(Path.GetFileNameWithoutExtension(filePath));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or FormatException)
        {
            errorMessage = $"Unable to read JSONL session archive: {ex.Message}";
            return false;
        }
    }

    public static bool TryReadJsonLinesArchive(
        ZipArchive archive,
        string sourceName,
        out SessionArchiveJsonLinesPayload? payload,
        out string? errorMessage)
    {
        using var reader = new SystemZipSessionArchiveReader(archive);
        return TryReadJsonLinesArchive(reader, sourceName, out payload, out errorMessage);
    }

    public static bool TryReadJsonLinesArchive(
        ISessionArchiveReader archive,
        string sourceName,
        out SessionArchiveJsonLinesPayload? payload,
        out string? errorMessage)
    {
        payload = null;
        errorMessage = null;

        try
        {
            var builder = new JsonLinesSessionBuilder(Path.GetFileNameWithoutExtension(sourceName));
            ApplyOfflineCaptureArchiveMetadata(archive, builder);
            ApplyOfflineAnnotatedFeedbackBundles(archive, builder);
            ApplyNetworkRequestDocuments(archive, builder);

            var jsonLinesEntries = archive.Entries
                .Where(entry => !string.IsNullOrWhiteSpace(entry.Name)
                                && IsJsonLinesFilePath(entry.Name)
                                && !entry.FullName.StartsWith(ArtifactsArchiveDirectoryName + "/", StringComparison.Ordinal))
                .OrderBy(entry => entry.FullName, StringComparer.Ordinal)
                .ToArray();

            if (jsonLinesEntries.Length == 0 && !builder.HasAnyEvidence)
            {
                errorMessage = "The selected archive is missing session.json and does not contain JSONL session records.";
                return false;
            }

            foreach (var entry in jsonLinesEntries)
            {
                using var stream = entry.Open();
                ReadJsonLines(stream, entry.FullName, builder);
            }

            ApplyOfflineCaptureScreenshotIndexes(archive, builder);

            if (!builder.HasAnyEvidence)
            {
                errorMessage = "The selected archive did not contain any supported JSONL session records.";
                return false;
            }

            var builtPayload = builder.Build(Path.GetFileNameWithoutExtension(sourceName));
            var imageBytesByFrameId = new Dictionary<string, byte[]>(builtPayload.ImageBytesByFrameId, StringComparer.Ordinal);
            foreach (var frame in builtPayload.Snapshot.Images)
            {
                if (imageBytesByFrameId.ContainsKey(frame.FrameId))
                {
                    continue;
                }

                var imageEntry = archive.GetEntry(SessionImageArtifactPath.ResolveArchiveEntryPath(frame));
                if (imageEntry is null)
                {
                    continue;
                }

                using var imageStream = imageEntry.Open();
                using var imageBuffer = new MemoryStream();
                imageStream.CopyTo(imageBuffer);
                imageBytesByFrameId[frame.FrameId] = imageBuffer.ToArray();
            }

            payload = new SessionArchiveJsonLinesPayload
            {
                Snapshot = RemoveImagesWithoutPayloads(builtPayload.Snapshot, imageBytesByFrameId),
                ImageBytesByFrameId = imageBytesByFrameId,
                ArtifactBytesByRelativePath = builtPayload.ArtifactBytesByRelativePath
            };
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or FormatException)
        {
            errorMessage = $"Unable to read JSONL session archive: {ex.Message}";
            return false;
        }
    }

    public static AppSessionSnapshot ApplyExternalArchivePayloads(
        ZipArchive archive,
        AppSessionSnapshot snapshot,
        IReadOnlyList<SessionVisualTreeSnapshotIndexEntry>? visualTreeSnapshotIndex = null)
    {
        using var reader = new SystemZipSessionArchiveReader(archive);
        return ApplyExternalArchivePayloads(reader, snapshot, visualTreeSnapshotIndex);
    }

    public static AppSessionSnapshot ApplyExternalArchivePayloads(
        ISessionArchiveReader archive,
        AppSessionSnapshot snapshot,
        IReadOnlyList<SessionVisualTreeSnapshotIndexEntry>? visualTreeSnapshotIndex = null)
    {
        var legacyLogs = ReadArchiveJsonEntry<SessionArchiveExternalLogsDocument>(archive, SessionLogsArchiveEntryPath)?.Logs
                         ?? snapshot.Logs;
        var logStreams = SessionLogStreamArchiveCodec.TryRead(archive, ArchiveJsonOptions)
                         ?? SessionLogStreams.Normalize(snapshot.LogStreams, legacyLogs);
        var logs = SessionLogStreams.Flatten(logStreams);
        var telemetry = ReadArchiveJsonEntry<SessionArchiveExternalTelemetryDocument>(archive, SessionTelemetryArchiveEntryPath);
        var touchDocument = ReadArchiveJsonEntry<SessionArchiveExternalTouchesDocument>(archive, SessionTouchesArchiveEntryPath);
        var touches = touchDocument is null
            ? snapshot.Touches
            : SessionTouchPacking.Unpack(touchDocument.Batches);
        var metricChannels = telemetry?.Channels ?? snapshot.MetricChannels;
        var metrics = telemetry?.Metrics ?? snapshot.Metrics;
        var visualTreeSnapshots = visualTreeSnapshotIndex is { Count: > 0 }
            ? SessionArchiveVisualTreeCodec.Read(archive, visualTreeSnapshotIndex)
            : snapshot.VisualTreeSnapshots;

        var builder = new JsonLinesSessionBuilder(snapshot.SessionId);
        foreach (var entryPath in JsonLinesArchiveEntryPaths)
        {
            var entry = archive.GetEntry(entryPath);
            if (entry is null)
            {
                continue;
            }

            using var stream = entry.Open();
            ReadJsonLines(stream, entry.FullName, builder);
        }

        if (builder.HasLogs && archive.GetEntry(SessionLogStreamArchiveCodec.IndexEntryPath) is null)
        {
            logs = builder.Logs;
            logStreams = SessionLogStreams.Normalize(Array.Empty<SessionLogStream>(), logs);
        }

        if (builder.HasMetricChannels)
        {
            metricChannels = builder.MetricChannels;
        }

        if (builder.HasMetrics)
        {
            metrics = builder.Metrics;
        }

        if (builder.HasTouches)
        {
            touches = builder.Touches;
        }

        ApplyNetworkRequestDocuments(archive, builder);

        return CopySnapshot(
            snapshot,
            logStreams,
            logs,
            metricChannels,
            metrics,
            touches,
            builder.HasNetworkRequests ? builder.NetworkRequests : snapshot.NetworkRequests,
            visualTreeSnapshots);
    }

    private static void ApplyOfflineCaptureArchiveMetadata(
        ISessionArchiveReader archive,
        JsonLinesSessionBuilder builder)
    {
        foreach (var entry in archive.Entries.OrderBy(entry => entry.FullName, StringComparer.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(entry.Name))
            {
                continue;
            }

            if (entry.FullName.EndsWith("manifest.json", StringComparison.OrdinalIgnoreCase))
            {
                ReadArchiveJsonElement(entry, root => ApplyOfflineCaptureManifest(root, builder));
            }
            else if (entry.FullName.EndsWith("metadata/channels.json", StringComparison.OrdinalIgnoreCase))
            {
                ReadArchiveJsonElement(entry, root => ApplyOfflineCaptureChannels(root, builder));
            }
            else if (entry.FullName.EndsWith("metadata/device-profile.json", StringComparison.OrdinalIgnoreCase))
            {
                ReadArchiveJsonElement(entry, root => ApplyOfflineCaptureDeviceProfile(root, builder));
            }
            else if (entry.FullName.EndsWith("metadata/custom-properties.json", StringComparison.OrdinalIgnoreCase))
            {
                ReadArchiveJsonElement(entry, root => ApplyOfflineCaptureCustomProperties(root, builder));
            }
        }
    }

    private static void ApplyNetworkRequestDocuments(
        ISessionArchiveReader archive,
        JsonLinesSessionBuilder builder)
    {
        foreach (var entry in archive.Entries
                     .Where(entry => !string.IsNullOrWhiteSpace(entry.Name)
                                     && entry.FullName.Contains("network/requests/", StringComparison.OrdinalIgnoreCase)
                                     && entry.FullName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                     .OrderBy(entry => entry.FullName, StringComparer.Ordinal))
        {
            try
            {
                using var stream = entry.Open();
                var request = JsonSerializer.Deserialize<SessionNetworkRequest>(stream, ArchiveJsonOptions);
                if (request is not null)
                {
                    builder.AddNetworkRequest(request);
                }
            }
            catch (JsonException)
            {
                // Preserve the rest of the capture when an individual request file is malformed.
            }
        }
    }

    private static void ApplyOfflineCaptureManifest(JsonElement root, JsonLinesSessionBuilder builder)
    {
        builder.SessionId ??= FirstString(root, "sessionId");
        builder.AppId ??= FirstString(root, "appId");
        builder.ClientName ??= FirstString(root, "clientName");
        builder.RemoteAddress ??= FirstString(root, "remoteAddress");
        builder.ProcessSessionId ??= FirstString(root, "processSessionId");
        builder.CaptureSource ??= FirstString(root, "captureSource");
        builder.SdkVersion ??= FirstString(root, "sdkVersion");
        builder.CreatedUtc ??= FirstTimestamp(root, "startedAtUtc", "createdUtc");
        builder.LastUpdatedUtc ??= FirstTimestamp(root, "stoppedAtUtc", "lastUpdatedUtc");
        builder.Status ??= builder.LastUpdatedUtc is null ? "Offline Capture" : "Offline Capture Complete";

        var appState = FirstString(root, "appState");
        if (!string.IsNullOrWhiteSpace(appState)
            && Enum.TryParse<AppLifecycleState>(appState, ignoreCase: true, out var parsedAppState))
        {
            builder.AppState = parsedAppState;
        }

        builder.AppStateChangedUtc ??= FirstTimestamp(root, "appStateChangedUtc");
    }

    private static void ApplyOfflineCaptureChannels(JsonElement root, JsonLinesSessionBuilder builder)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var channelElement in root.EnumerateArray())
            {
                if (TryCreateMetricChannel(channelElement, out var channel))
                {
                    builder.AddMetricChannel(channel);
                }
            }

            return;
        }

        if (!TryGetProperty(root, "ch", out var channelsElement) || channelsElement.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var channelElement in channelsElement.EnumerateArray())
        {
            if (TryCreateMetricChannel(channelElement, out var channel))
            {
                builder.AddMetricChannel(channel);
            }
        }
    }

    private static void ApplyOfflineCaptureDeviceProfile(JsonElement root, JsonLinesSessionBuilder builder)
    {
        builder.DeviceProfileJson = root.GetRawText();
        if (TryDeserialize(root, out DeviceAppProfile? profile))
        {
            builder.DeviceProfile = profile;
        }
    }

    private static void ApplyOfflineCaptureCustomProperties(JsonElement root, JsonLinesSessionBuilder builder)
    {
        try
        {
            builder.CustomProperties = JsonSerializer.Deserialize<JsonObject>(root.GetRawText(), ArchiveJsonOptions);
        }
        catch (JsonException)
        {
            builder.CustomProperties = null;
        }
    }

    private static void ApplyOfflineCaptureScreenshotIndexes(
        ISessionArchiveReader archive,
        JsonLinesSessionBuilder builder)
    {
        foreach (var entry in archive.Entries
                     .Where(entry => !string.IsNullOrWhiteSpace(entry.Name)
                                     && IsJsonLinesFilePath(entry.Name)
                                     && entry.FullName.Contains("screenshots/index/", StringComparison.OrdinalIgnoreCase))
                     .OrderBy(entry => entry.FullName, StringComparer.Ordinal))
        {
            var indexStart = entry.FullName.IndexOf("screenshots/index/", StringComparison.OrdinalIgnoreCase);
            var sessionRoot = indexStart <= 0 ? string.Empty : entry.FullName[..indexStart];
            using var stream = entry.Open();
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            while (reader.ReadLine() is { } line)
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0)
                {
                    continue;
                }

                using var document = JsonDocument.Parse(trimmed, JsonDocumentOptions);
                ApplyOfflineCaptureScreenshotIndex(archive, sessionRoot, document.RootElement, builder);
            }
        }
    }

    private static void ApplyOfflineAnnotatedFeedbackBundles(
        ISessionArchiveReader archive,
        JsonLinesSessionBuilder builder)
    {
        foreach (var entry in archive.Entries
                     .Where(entry => !string.IsNullOrWhiteSpace(entry.Name)
                                     && entry.FullName.Contains("annotations/bundles/", StringComparison.OrdinalIgnoreCase)
                                     && entry.FullName.EndsWith(".ansightannotation", StringComparison.OrdinalIgnoreCase))
                     .OrderBy(entry => entry.FullName, StringComparer.Ordinal))
        {
            try
            {
                if (entry.Length < 0 || entry.Length > AnnotatedFeedbackBundleReader.MaximumBundleBytes)
                {
                    continue;
                }

                using var entryStream = entry.Open();
                using var bundleStream = new MemoryStream(
                    entry.Length > 0 ? checked((int)Math.Min(entry.Length, int.MaxValue)) : 0);
                entryStream.CopyTo(bundleStream);
                if (bundleStream.Length > AnnotatedFeedbackBundleReader.MaximumBundleBytes)
                {
                    continue;
                }

                bundleStream.Position = 0;
                if (AnnotatedFeedbackBundleReader.TryRead(
                        bundleStream,
                        entry.FullName,
                        out var content,
                        out _)
                    && content is not null)
                {
                    builder.AddAnnotatedFeedback(content);
                }
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or JsonException or UnauthorizedAccessException)
            {
                // A malformed or disallowed evidence bundle must not prevent the rest of an offline session from importing.
            }
        }
    }

    private static void ApplyOfflineCaptureScreenshotIndex(
        ISessionArchiveReader archive,
        string sessionRoot,
        JsonElement root,
        JsonLinesSessionBuilder builder)
    {
        var relativePath = FirstString(root, "path", "p");
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return;
        }

        var normalizedRelativePath = relativePath.Replace('\\', '/').TrimStart('/');
        var imageEntry = archive.GetEntry(sessionRoot + normalizedRelativePath)
                         ?? archive.GetEntry(normalizedRelativePath);
        if (imageEntry is null)
        {
            return;
        }

        using var imageStream = imageEntry.Open();
        using var imageBuffer = new MemoryStream();
        imageStream.CopyTo(imageBuffer);
        var bytes = imageBuffer.ToArray();
        if (bytes.Length == 0)
        {
            return;
        }

        var capturedAtUtc = FirstTimestamp(root, "capturedAtUtc", "timestampUtc", "timestamp", "t")
                            ?? DateTimeOffset.UtcNow;
        var frameId = FirstString(root, "frameId", "id")
                      ?? Path.GetFileNameWithoutExtension(normalizedRelativePath)
                      ?? BuildFrameId(capturedAtUtc, builder.Images.Count);
        var format = NormalizeImageFormat(Path.GetExtension(normalizedRelativePath));

        builder.AddImage(
            new SessionImageFrame
            {
                FrameId = frameId.Trim(),
                CapturedAtUtc = capturedAtUtc,
                Format = format,
                Width = Math.Max(0, FirstInt(root, "width", "w") ?? 0),
                Height = Math.Max(0, FirstInt(root, "height", "h") ?? 0),
                Quality = Math.Clamp(FirstInt(root, "quality", "q") ?? 80, 0, 100),
                ByteCount = bytes.Length
            },
            bytes);
    }

    private static void ReadArchiveJsonElement(ISessionArchiveEntry entry, Action<JsonElement> apply)
    {
        using var stream = entry.Open();
        using var document = JsonDocument.Parse(stream, JsonDocumentOptions);
        apply(document.RootElement);
    }

    private static void ReadJsonLines(Stream stream, string sourceName, JsonLinesSessionBuilder builder)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var lineNumber = 0;
        while (reader.ReadLine() is { } line)
        {
            lineNumber++;
            var trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(trimmed, JsonDocumentOptions);
                ApplyJsonLineElement(document.RootElement, builder);
            }
            catch (JsonException ex)
            {
                throw new JsonException($"Invalid JSONL record in '{sourceName}' at line {lineNumber}: {ex.Message}", ex);
            }
        }
    }

    private static void ApplyJsonLineElement(JsonElement element, JsonLinesSessionBuilder builder)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in element.EnumerateArray())
            {
                ApplyJsonLineElement(child, builder);
            }

            return;
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        ApplySessionMetadata(element, builder);
        ApplySessionObject(element, builder);
        ApplyDeviceProfile(element, builder);
        ApplyAppState(element, builder);
        ApplyLogs(element, builder);
        ApplyMetricChannels(element, builder);
        ApplyMetrics(element, builder);
        ApplyTouches(element, builder);
        ApplyNetworkRequest(element, builder);
        ApplyImage(element, builder);
    }

}
