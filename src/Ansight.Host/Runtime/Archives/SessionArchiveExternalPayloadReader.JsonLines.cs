using System.IO.Compression;
using System.Text.Json.Nodes;
using Ansight.Pairing.Models;
using static Ansight.Host.Runtime.Archives.SessionArchiveJsonValueReader;

namespace Ansight.Host.Runtime.Archives;

internal static partial class SessionArchiveExternalPayloadReader
{
    private static void ApplySessionObject(JsonElement root, JsonLinesSessionBuilder builder)
    {
        if (TryGetProperty(root, "session", out var sessionElement)
            && sessionElement.ValueKind == JsonValueKind.Object
            && TryDeserialize(sessionElement, out AppSessionSnapshot? nestedSnapshot)
            && nestedSnapshot is not null)
        {
            builder.ApplySnapshot(nestedSnapshot);
        }

        var schema = TryGetString(root, "schema");
        if (SessionCaptureDocument.IsSupportedSchema(schema)
            && TryDeserialize(root, out SessionCaptureDocument? document)
            && document?.Session is not null)
        {
            var session = document.Session.Author is null && document.Author is not null
                ? CopySnapshotWithAuthor(document.Session, document.Author)
                : document.Session;
            builder.ApplySnapshot(session);
            return;
        }

        var type = TryGetString(root, "type");
        if (!IsWireEventType(type)
            && TryGetProperty(root, "sessionId", out _)
            && TryGetProperty(root, "appId", out _)
            && TryDeserialize(root, out AppSessionSnapshot? snapshot)
            && snapshot is not null)
        {
            builder.ApplySnapshot(snapshot);
        }
    }

    private static void ApplySessionMetadata(JsonElement root, JsonLinesSessionBuilder builder)
    {
        builder.SessionId ??= FirstString(root, "sessionId");
        builder.AppId ??= FirstString(root, "appId", "bundleId", "packageName");
        builder.ClientName ??= FirstString(root, "clientName", "appName");
        builder.RemoteAddress ??= FirstString(root, "remoteAddress");
        builder.ConfigId ??= FirstString(root, "configId");
        builder.ProcessSessionId ??= FirstString(root, "processSessionId");
        builder.Status ??= FirstString(root, "status");
        builder.CaptureSource ??= FirstString(root, "captureSource");
        builder.SdkVersion ??= FirstString(root, "sdkVersion");
        builder.Notes ??= FirstString(root, "notes");
        builder.CreatedUtc ??= FirstTimestamp(root, "createdUtc", "startedAtUtc", "startUtc");
        builder.LastUpdatedUtc ??= FirstTimestamp(root, "lastUpdatedUtc", "endedAtUtc", "updatedUtc");
    }

    private static void ApplyDeviceProfile(JsonElement root, JsonLinesSessionBuilder builder)
    {
        var type = TryGetString(root, "type");
        if (!string.Equals(type, WebSocketClientEventConstants.DeviceAppProfile, StringComparison.Ordinal))
        {
            return;
        }

        var rawJson = root.GetRawText();
        builder.DeviceProfileJson = rawJson;
        if (TryDeserialize(root, out DeviceAppProfile? profile))
        {
            builder.DeviceProfile = profile;
        }
    }

    private static void ApplyAppState(JsonElement root, JsonLinesSessionBuilder builder)
    {
        var type = TryGetString(root, "type");
        if (!string.Equals(type, WebSocketClientEventConstants.ClientAppState, StringComparison.Ordinal))
        {
            return;
        }

        var state = TryGetString(root, "state")?.Trim().ToLowerInvariant() switch
        {
            "foreground" => AppLifecycleState.Foreground,
            "background" => AppLifecycleState.Background,
            "unknown" => AppLifecycleState.Unknown,
            _ => (AppLifecycleState?)null
        };

        if (state is null)
        {
            return;
        }

        builder.AppState = state.Value;
        builder.AppStateChangedUtc = FirstTimestamp(root, "changedAtUtc", "capturedAtUtc", "timestampUtc", "timestamp")
                                     ?? DateTimeOffset.UtcNow;
    }

    private static void ApplyLogs(JsonElement root, JsonLinesSessionBuilder builder)
    {
        if (TryGetProperty(root, "logs", out var logsElement) && logsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var logElement in logsElement.EnumerateArray())
            {
                if (TryCreateLogEntry(logElement, out var logEntry))
                {
                    builder.AddLog(logEntry);
                }
            }
        }

        var type = TryGetString(root, "type");
        if (string.Equals(type, WebSocketClientEventConstants.ClientEvents, StringComparison.Ordinal)
            || TryGetProperty(root, "events", out _))
        {
            ApplyClientEvents(root, builder);
        }

        if (TryCreateLogEntry(root, out var rootLogEntry)
            && (string.Equals(type, WebSocketClientEventConstants.ClientLog, StringComparison.Ordinal)
                || !TryGetProperty(root, "logs", out _)))
        {
            builder.AddLog(rootLogEntry);
        }
    }

    private static void ApplyClientEvents(JsonElement root, JsonLinesSessionBuilder builder)
    {
        if (!TryGetProperty(root, "events", out var eventsElement) || eventsElement.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var eventElement in eventsElement.EnumerateArray())
        {
            var label = FirstString(eventElement, "label", "message", "name");
            if (string.IsNullOrWhiteSpace(label))
            {
                continue;
            }

            var id = FirstString(eventElement, "id", "eventId");
            var eventType = FirstString(eventElement, "eventType", "type") ?? "Event";
            var details = FirstString(eventElement, "details", "data") ?? string.Empty;
            var capturedAtUtc = FirstTimestamp(eventElement, "capturedAtUtc", "timestampUtc", "timestamp")
                                ?? DateTimeOffset.UtcNow;
            var channelIdValue = FirstInt(eventElement, "channelId", "channel");
            var channelId = channelIdValue is >= byte.MinValue and <= byte.MaxValue
                ? (byte)channelIdValue.Value
                : (byte)0;
            var eventId = string.IsNullOrWhiteSpace(id) ? Guid.NewGuid().ToString("D") : id.Trim();
            builder.AddApplicationEvent(new SessionApplicationEvent(
                eventId,
                label.Trim(),
                eventType.Trim(),
                details.Trim(),
                capturedAtUtc.ToUniversalTime(),
                channelId));
            var message = string.IsNullOrWhiteSpace(details)
                ? label.Trim()
                : $"{label.Trim()}: {details.Trim()}";

            builder.AddLog(new LogEntry(capturedAtUtc, message)
            {
                Source = "Client",
                Tag = eventType.Trim().ToUpperInvariant(),
                EventId = eventId,
                Priority = LogPriority.Information
            });
        }
    }

    private static void ApplyMetricChannels(JsonElement root, JsonLinesSessionBuilder builder)
    {
        if (TryGetProperty(root, "channels", out var channelsElement) && channelsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var channelElement in channelsElement.EnumerateArray())
            {
                if (TryCreateMetricChannel(channelElement, out var channel))
                {
                    builder.AddMetricChannel(channel);
                }
            }
        }

        if (TryGetProperty(root, "channel", out var nestedChannelElement)
            && nestedChannelElement.ValueKind == JsonValueKind.Object
            && TryCreateMetricChannel(nestedChannelElement, out var nestedChannel))
        {
            builder.AddMetricChannel(nestedChannel);
        }

        if (TryCreateMetricChannel(root, out var rootChannel))
        {
            builder.AddMetricChannel(rootChannel);
        }
    }

    private static void ApplyMetrics(JsonElement root, JsonLinesSessionBuilder builder)
    {
        if (TryGetProperty(root, "metrics", out var metricsElement) && metricsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var metricElement in metricsElement.EnumerateArray())
            {
                if (TryCreateMetricSample(metricElement, out var sample))
                {
                    builder.AddMetric(sample);
                }
            }
        }

        foreach (var propertyName in new[] { "metric", "sample" })
        {
            if (TryGetProperty(root, propertyName, out var nestedMetricElement)
                && nestedMetricElement.ValueKind == JsonValueKind.Object
                && TryCreateMetricSample(nestedMetricElement, out var nestedMetric))
            {
                builder.AddMetric(nestedMetric);
            }
        }

        if (TryCreateMetricSample(root, out var rootSample))
        {
            builder.AddMetric(rootSample);
        }
    }

    private static void ApplyTouches(JsonElement root, JsonLinesSessionBuilder builder)
    {
        if (TryGetProperty(root, "batches", out var batchesElement)
            && batchesElement.ValueKind == JsonValueKind.Array
            && TryDeserialize(batchesElement, out List<SessionTouchPackedBatch>? batches)
            && batches is not null)
        {
            foreach (var touch in SessionTouchPacking.Unpack(batches))
            {
                builder.AddTouch(touch);
            }
        }

        if (string.Equals(TryGetString(root, "schema"), SessionTouchPacking.SchemaName, StringComparison.Ordinal)
            && TryGetProperty(root, "rows", out _)
            && TryDeserialize(root, out SessionTouchPackedBatch? batch)
            && batch is not null)
        {
            foreach (var touch in SessionTouchPacking.Unpack([batch]))
            {
                builder.AddTouch(touch);
            }
        }

        if (TryGetProperty(root, "touches", out var touchesElement) && touchesElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var touchElement in touchesElement.EnumerateArray())
            {
                if (TryCreateTouchInputRecord(touchElement, out var touch))
                {
                    builder.AddTouch(touch);
                }
            }
        }

        if (TryGetProperty(root, "touch", out var nestedTouchElement)
            && nestedTouchElement.ValueKind == JsonValueKind.Object
            && TryCreateTouchInputRecord(nestedTouchElement, out var nestedTouch))
        {
            builder.AddTouch(nestedTouch);
        }

        if (TryCreateTouchInputRecord(root, out var rootTouch))
        {
            builder.AddTouch(rootTouch);
        }
    }

    private static void ApplyNetworkRequest(JsonElement root, JsonLinesSessionBuilder builder)
    {
        var requestElement = root;
        if (string.Equals(
                TryGetString(root, "type"),
                WebSocketClientEventConstants.ClientNetworkRequest,
                StringComparison.Ordinal))
        {
            if (!TryGetProperty(root, "request", out requestElement)
                || requestElement.ValueKind != JsonValueKind.Object)
            {
                return;
            }
        }
        else if (!string.Equals(
                     TryGetString(root, "schema"),
                     SessionNetworkRequest.SchemaName,
                     StringComparison.Ordinal))
        {
            return;
        }

        if (TryDeserialize(requestElement, out SessionNetworkRequest? request) && request is not null)
        {
            builder.AddNetworkRequest(request);
        }
    }

    private static void ApplyImage(JsonElement root, JsonLinesSessionBuilder builder)
    {
        var type = TryGetString(root, "type");
        if (!string.Equals(type, WebSocketClientEventConstants.ClientJpeg, StringComparison.Ordinal)
            && !TryGetProperty(root, "base64", out _)
            && !TryGetProperty(root, "dataBase64", out _))
        {
            return;
        }

        var base64 = FirstString(root, "base64", "dataBase64", "data", "bytes");
        if (string.IsNullOrWhiteSpace(base64))
        {
            return;
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(base64.Trim());
        }
        catch (FormatException)
        {
            return;
        }

        if (bytes.Length == 0)
        {
            return;
        }

        var capturedAtUtc = FirstTimestamp(root, "capturedAtUtc", "timestampUtc", "timestamp")
                            ?? DateTimeOffset.UtcNow;
        var format = FirstString(root, "format", "mimeType") ?? "jpeg";
        format = NormalizeImageFormat(format);
        var frameId = FirstString(root, "frameId", "id") ?? BuildFrameId(capturedAtUtc, builder.Images.Count);
        var width = FirstInt(root, "width") ?? 0;
        var height = FirstInt(root, "height") ?? 0;
        var quality = FirstInt(root, "quality") ?? 80;

        builder.AddImage(
            new SessionImageFrame
            {
                FrameId = frameId.Trim(),
                CapturedAtUtc = capturedAtUtc,
                Format = format,
                Width = Math.Max(0, width),
                Height = Math.Max(0, height),
                Quality = Math.Clamp(quality, 0, 100),
                ByteCount = bytes.Length
            },
            bytes);
    }

    private static bool TryCreateLogEntry(JsonElement element, out LogEntry logEntry)
    {
        logEntry = default!;
        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var type = TryGetString(element, "type");
        var message = string.Equals(type, WebSocketClientEventConstants.ClientLog, StringComparison.Ordinal)
            ? FirstString(element, "message", "data", "log", "text", "l")
            : FirstString(element, "message", "label", "log", "text", "l");
        if (string.IsNullOrWhiteSpace(message))
        {
            return false;
        }

        var details = FirstString(element, "details", "d");
        if (!string.IsNullOrWhiteSpace(details))
        {
            message = $"{message.Trim()}: {details.Trim()}";
        }

        var timestamp = FirstTimestamp(element, "timestampUtc", "capturedAtUtc", "timestamp", "time", "t")
                        ?? DateTimeOffset.UtcNow;
        var priority = TryGetLogPriority(element) ?? LogPriority.Information;
        var source = FirstString(element, "source") ?? "Client";
        var tag = FirstString(element, "tag", "category", "eventType");
        if (string.IsNullOrWhiteSpace(tag) && FirstInt(element, "k") is { } eventKind)
        {
            tag = ResolveOfflineEventKind(eventKind);
        }

        if (string.IsNullOrWhiteSpace(tag))
        {
            tag = string.IsNullOrWhiteSpace(type)
                ? string.Empty
                : type.Trim();
        }

        logEntry = new LogEntry(timestamp, message.Trim())
        {
            Source = source.Trim(),
            Tag = tag,
            EventId = FirstString(element, "eventId", "id"),
            ProcessId = FirstInt(element, "processId"),
            ThreadId = FirstInt(element, "threadId"),
            Priority = priority
        };
        return true;
    }

    private static bool TryCreateMetricChannel(JsonElement element, out SessionMetricChannel channel)
    {
        channel = default!;
        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var channelId = FirstByte(element, "channelId", "id", "channel");
        var name = FirstString(element, "name", "n");
        if (channelId is null || string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        var colorHex = FirstString(element, "colorHex", "color");
        if (string.IsNullOrWhiteSpace(colorHex)
            && TryGetProperty(element, "c", out var compactColorElement)
            && compactColorElement.ValueKind == JsonValueKind.String)
        {
            colorHex = compactColorElement.GetString();
        }

        channel = new SessionMetricChannel
        {
            ChannelId = channelId.Value,
            Name = name.Trim(),
            ColorHex = NormalizeColorHex(colorHex),
            Unit = NormalizeOptionalString(FirstString(element, "unit")),
            Type = NormalizeOptionalString(FirstString(element, "type")) ?? "custom",
            Source = NormalizeOptionalString(FirstString(element, "source")),
            Group = NormalizeOptionalString(FirstString(element, "group")),
            Kind = NormalizeOptionalString(FirstString(element, "kind"))
        };
        return true;
    }

    private static bool TryCreateMetricSample(JsonElement element, out SessionMetricSample sample)
    {
        sample = default!;
        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var channelId = FirstByte(element, "channelId", "channel", "c", "id");
        var value = FirstInt64(element, "value", "v");
        if (channelId is null || value is null)
        {
            return false;
        }

        sample = new SessionMetricSample
        {
            ChannelId = channelId.Value,
            Value = value.Value,
            CapturedAtUtc = FirstTimestamp(element, "capturedAtUtc", "timestampUtc", "timestamp", "time", "t")
                            ?? DateTimeOffset.UtcNow,
            SegmentId = FirstInt(element, "segmentId") ?? 0
        };
        return true;
    }

    private static bool TryCreateTouchInputRecord(JsonElement element, out SessionTouchInputRecord touch)
    {
        touch = default!;
        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var action = FirstString(element, "action", "phase", "a") ?? "unknown";
        var x = FirstDouble(element, "x", "clientX");
        var y = FirstDouble(element, "y", "clientY");
        if (x is null || y is null)
        {
            return false;
        }

        var capturedAtUtc = FirstTimestamp(element, "capturedAtUtc", "timestampUtc", "timestamp", "time", "t")
                            ?? DateTimeOffset.UtcNow;
        var pointerIndex = FirstInt(element, "pointerIndex", "i") ?? 0;
        var pointerCount = Math.Max(pointerIndex + 1, FirstInt(element, "pointerCount", "pc") ?? 1);
        var pointerId = FirstInt64(element, "pointerId", "pointer", "p") ?? 0;
        SessionTouchSampleDetails? details = null;
        if (TryGetProperty(element, "details", out var detailsElement))
        {
            TryDeserialize(detailsElement, out details);
        }

        touch = new SessionTouchInputRecord
        {
            Id = FirstString(element, "id", "touchId")
                 ?? BuildTouchId(capturedAtUtc, pointerId),
            Action = NormalizeTouchAction(action),
            CapturedAtUtc = capturedAtUtc,
            PointerId = pointerId,
            PointerIndex = Math.Max(0, pointerIndex),
            PointerCount = pointerCount,
            X = x.Value,
            Y = y.Value,
            NormalizedX = FirstDouble(element, "normalizedX"),
            NormalizedY = FirstDouble(element, "normalizedY"),
            SurfaceWidth = FirstDouble(element, "surfaceWidth", "width", "w"),
            SurfaceHeight = FirstDouble(element, "surfaceHeight", "height", "h"),
            CoordinateSpace = NormalizeCoordinateSpace(FirstString(element, "coordinateSpace", "space")),
            CoordinateUnit = NormalizeCoordinateUnit(FirstString(element, "coordinateUnit", "unit", "u")),
            SurfaceScale = FirstDouble(element, "surfaceScale", "scale", "s"),
            Details = details
        };
        return true;
    }

    private static T? ReadArchiveJsonEntry<T>(ISessionArchiveReader archive, string entryPath)
        where T : class
    {
        var entry = archive.GetEntry(entryPath);
        if (entry is null)
        {
            return null;
        }

        using var stream = entry.Open();
        return JsonSerializer.Deserialize<T>(stream, ArchiveJsonOptions);
    }

    private static AppSessionSnapshot CopySnapshot(
        AppSessionSnapshot snapshot,
        IReadOnlyList<SessionLogStream> logStreams,
        IReadOnlyList<LogEntry> logs,
        IReadOnlyList<SessionMetricChannel> metricChannels,
        IReadOnlyList<SessionMetricSample> metrics,
        IReadOnlyList<SessionTouchInputRecord> touches,
        IReadOnlyList<SessionNetworkRequest> networkRequests,
        IReadOnlyList<SessionVisualTreeSnapshot> visualTreeSnapshots)
    {
        return new AppSessionSnapshot
        {
            SessionId = snapshot.SessionId,
            AppId = snapshot.AppId,
            ClientName = snapshot.ClientName,
            RemoteAddress = snapshot.RemoteAddress,
            CreatedUtc = snapshot.CreatedUtc,
            ConfigId = snapshot.ConfigId,
            ProcessSessionId = snapshot.ProcessSessionId,
            Status = snapshot.Status,
            LastUpdatedUtc = snapshot.LastUpdatedUtc,
            IsHistorical = snapshot.IsHistorical,
            CacheSizeBytes = snapshot.CacheSizeBytes,
            IsPinned = snapshot.IsPinned,
            Author = snapshot.Author,
            ReplaySource = snapshot.ReplaySource,
            CaptureSource = snapshot.CaptureSource,
            SdkVersion = snapshot.SdkVersion,
            Name = snapshot.Name,
            Tags = snapshot.Tags,
            Notes = snapshot.Notes,
            CustomProperties = SessionSnapshotCloner.CloneCustomProperties(snapshot.CustomProperties),
            AppState = snapshot.AppState,
            AppStateChangedUtc = snapshot.AppStateChangedUtc,
            DeviceProfile = snapshot.DeviceProfile,
            DeviceProfileJson = snapshot.DeviceProfileJson,
            AppIcon = snapshot.AppIcon,
            AppToolCatalog = SessionSnapshotCloner.CloneAppToolCatalog(snapshot.AppToolCatalog),
            Analyses = snapshot.Analyses,
            Annotations = snapshot.Annotations,
            AgentTaskLinks = snapshot.AgentTaskLinks,
            Images = snapshot.Images,
            Touches = touches,
            NetworkRequests = networkRequests,
            VisualTreeSnapshots = visualTreeSnapshots,
            ArtifactSnapshots = snapshot.ArtifactSnapshots,
            ApplicationEvents = snapshot.ApplicationEvents,
            LogStreams = logStreams,
            Logs = logs,
            MetricChannels = metricChannels,
            Metrics = metrics,
            TotalApplicationEventCount = snapshot.TotalApplicationEventCount,
            TotalNetworkRequestCount = Math.Max(snapshot.TotalNetworkRequestCount, networkRequests.Count)
        };
    }

    private static AppSessionSnapshot CopySnapshotWithAuthor(AppSessionSnapshot snapshot, SessionCaptureAuthorMetadata author)
    {
        return new AppSessionSnapshot
        {
            SessionId = snapshot.SessionId,
            AppId = snapshot.AppId,
            ClientName = snapshot.ClientName,
            RemoteAddress = snapshot.RemoteAddress,
            CreatedUtc = snapshot.CreatedUtc,
            ConfigId = snapshot.ConfigId,
            ProcessSessionId = snapshot.ProcessSessionId,
            Status = snapshot.Status,
            LastUpdatedUtc = snapshot.LastUpdatedUtc,
            IsHistorical = snapshot.IsHistorical,
            CacheSizeBytes = snapshot.CacheSizeBytes,
            IsPinned = snapshot.IsPinned,
            Author = author,
            ReplaySource = snapshot.ReplaySource,
            CaptureSource = snapshot.CaptureSource,
            SdkVersion = snapshot.SdkVersion,
            Name = snapshot.Name,
            Tags = snapshot.Tags,
            Notes = snapshot.Notes,
            CustomProperties = SessionSnapshotCloner.CloneCustomProperties(snapshot.CustomProperties),
            AppState = snapshot.AppState,
            AppStateChangedUtc = snapshot.AppStateChangedUtc,
            DeviceProfile = snapshot.DeviceProfile,
            DeviceProfileJson = snapshot.DeviceProfileJson,
            AppIcon = snapshot.AppIcon,
            AppToolCatalog = SessionSnapshotCloner.CloneAppToolCatalog(snapshot.AppToolCatalog),
            Analyses = snapshot.Analyses,
            Annotations = snapshot.Annotations,
            AgentTaskLinks = snapshot.AgentTaskLinks,
            Images = snapshot.Images,
            Touches = snapshot.Touches,
            NetworkRequests = snapshot.NetworkRequests,
            VisualTreeSnapshots = snapshot.VisualTreeSnapshots,
            ArtifactSnapshots = snapshot.ArtifactSnapshots,
            ApplicationEvents = snapshot.ApplicationEvents,
            LogStreams = snapshot.LogStreams,
            Logs = snapshot.Logs,
            TotalLogCount = snapshot.TotalLogCount,
            RetainedLogStartIndex = snapshot.RetainedLogStartIndex,
            MetricChannels = snapshot.MetricChannels,
            Metrics = snapshot.Metrics,
            TotalApplicationEventCount = snapshot.TotalApplicationEventCount,
            TotalNetworkRequestCount = snapshot.TotalNetworkRequestCount
        };
    }

    internal static AppSessionSnapshot RemoveImagesWithoutPayloads(
        AppSessionSnapshot snapshot,
        IReadOnlyDictionary<string, byte[]> imageBytesByFrameId)
    {
        if (snapshot.Images.Count == 0)
        {
            return snapshot;
        }

        var imagesWithBytes = snapshot.Images
            .Where(frame => imageBytesByFrameId.TryGetValue(frame.FrameId, out var bytes) && bytes.Length > 0)
            .ToArray();
        if (imagesWithBytes.Length == snapshot.Images.Count)
        {
            return snapshot;
        }

        return new AppSessionSnapshot
        {
            SessionId = snapshot.SessionId,
            AppId = snapshot.AppId,
            ClientName = snapshot.ClientName,
            RemoteAddress = snapshot.RemoteAddress,
            CreatedUtc = snapshot.CreatedUtc,
            ConfigId = snapshot.ConfigId,
            ProcessSessionId = snapshot.ProcessSessionId,
            Status = snapshot.Status,
            LastUpdatedUtc = snapshot.LastUpdatedUtc,
            IsHistorical = snapshot.IsHistorical,
            CacheSizeBytes = snapshot.CacheSizeBytes,
            IsPinned = snapshot.IsPinned,
            Author = snapshot.Author,
            ReplaySource = snapshot.ReplaySource,
            CaptureSource = snapshot.CaptureSource,
            SdkVersion = snapshot.SdkVersion,
            Name = snapshot.Name,
            Tags = snapshot.Tags,
            Notes = snapshot.Notes,
            CustomProperties = SessionSnapshotCloner.CloneCustomProperties(snapshot.CustomProperties),
            AppState = snapshot.AppState,
            AppStateChangedUtc = snapshot.AppStateChangedUtc,
            DeviceProfile = snapshot.DeviceProfile,
            DeviceProfileJson = snapshot.DeviceProfileJson,
            AppIcon = snapshot.AppIcon,
            AppToolCatalog = SessionSnapshotCloner.CloneAppToolCatalog(snapshot.AppToolCatalog),
            Analyses = snapshot.Analyses,
            Annotations = snapshot.Annotations,
            AgentTaskLinks = snapshot.AgentTaskLinks,
            Images = imagesWithBytes,
            Touches = snapshot.Touches,
            NetworkRequests = snapshot.NetworkRequests,
            VisualTreeSnapshots = snapshot.VisualTreeSnapshots,
            ArtifactSnapshots = snapshot.ArtifactSnapshots,
            ApplicationEvents = snapshot.ApplicationEvents,
            LogStreams = snapshot.LogStreams,
            Logs = snapshot.Logs,
            TotalLogCount = snapshot.TotalLogCount,
            RetainedLogStartIndex = snapshot.RetainedLogStartIndex,
            MetricChannels = snapshot.MetricChannels,
            Metrics = snapshot.Metrics,
            TotalApplicationEventCount = snapshot.TotalApplicationEventCount,
            TotalNetworkRequestCount = snapshot.TotalNetworkRequestCount
        };
    }

}
