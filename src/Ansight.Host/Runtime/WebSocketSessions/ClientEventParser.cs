namespace Ansight.Host.Runtime.WebSocketSessions;

using System.Buffers;
using System.Globalization;
using System.Text;
using Ansight.Pairing;
using Ansight.Pairing.Models;
using Ansight.Tools;
using System.Text.Json.Nodes;
using Ansight.Host;
using Ansight.Host.Runtime.Operations;
using Ansight.Infrastructure.Preferences;
using AppLifecycleState = global::Ansight.AppLifecycleState;
using HostOperationResult = Ansight.Host.Runtime.Contracts.OperationResult;

internal static class ClientEventParser
{
    public static ParsedClientEvent ParseText(string payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload, JsonUtil.Document);
            var root = document.RootElement;
            var type = JsonUtil.TryGetString(root, "type");
            var data = JsonUtil.TryGetString(root, "data");
            var channels = ParseMetricChannels(root);
            var metrics = ParseMetricSamples(root);
            var events = ParseAppEvents(root);
            var touches = ParseTouchInputRecords(root);
            var networkRequest = ParseNetworkRequest(root);
            var appState = ParseAppLifecycleState(root);
            var appStateChangedUtc = ParseOptionalDateTimeOffset(root, "changedAtUtc");
            var deviceProfile = ParseDeviceAppProfile(payload, root);
            var imageFrame = ParseSessionImageFrame(root);
            var visualTreeSnapshot = ParseSessionVisualTreeSnapshot(root);
            var deviceProfileJson = string.Equals(type, WebSocketClientEventConstants.DeviceAppProfile, StringComparison.Ordinal)
                ? PrettyPrintJson(payload)
                : null;
            return new ParsedClientEvent(type, data, channels, metrics, events, touches, networkRequest, appState, appStateChangedUtc, deviceProfile, deviceProfileJson, imageFrame, visualTreeSnapshot);
        }
        catch
        {
            return new ParsedClientEvent(
                null,
                null,
                Array.Empty<SessionMetricChannel>(),
                Array.Empty<SessionMetricSample>(),
                Array.Empty<SessionApplicationEvent>(),
                Array.Empty<SessionTouchInputRecord>(),
                null,
                null,
                null,
                null,
                null,
                null,
                null);
        }
    }

    public static ParsedClientEvent ParseBinary(ReadOnlyMemory<byte> payload)
    {
        if (SessionJpegWireProtocol.TryParse(payload, out var capturedAtUtc, out var format, out var width, out var height, out var quality, out var bytes))
        {
            return new ParsedClientEvent(
                WebSocketClientEventConstants.ClientJpeg,
                null,
                Array.Empty<SessionMetricChannel>(),
                Array.Empty<SessionMetricSample>(),
                Array.Empty<SessionApplicationEvent>(),
                Array.Empty<SessionTouchInputRecord>(),
                null,
                null,
                null,
                null,
                null,
                new ParsedSessionImageFrame(capturedAtUtc, format, width, height, quality, bytes),
                null);
        }

        return new ParsedClientEvent(
            null,
            null,
            Array.Empty<SessionMetricChannel>(),
            Array.Empty<SessionMetricSample>(),
            Array.Empty<SessionApplicationEvent>(),
            Array.Empty<SessionTouchInputRecord>(),
            null,
            null,
            null,
            null,
            null,
            null,
            null);
    }

    private static SessionNetworkRequest? ParseNetworkRequest(JsonElement root)
    {
        if (!string.Equals(
                JsonUtil.TryGetString(root, "type"),
                WebSocketClientEventConstants.ClientNetworkRequest,
                StringComparison.Ordinal)
            || !root.TryGetProperty("request", out var requestElement)
            || requestElement.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        try
        {
            return SessionNetworkRequestSanitizer.Normalize(
                JsonSerializer.Deserialize<SessionNetworkRequest>(requestElement.GetRawText(), JsonUtil.Compact));
        }
        catch
        {
            return null;
        }
    }

    private static IReadOnlyList<SessionMetricChannel> ParseMetricChannels(JsonElement root)
    {
        if (!root.TryGetProperty("channels", out var channelsElement) || channelsElement.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<SessionMetricChannel>();
        }

        var channels = new List<SessionMetricChannel>();
        foreach (var channelElement in channelsElement.EnumerateArray())
        {
            var id = TryReadByte(channelElement, "id");
            var name = JsonUtil.TryGetString(channelElement, "name");
            if (id is null || string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            channels.Add(new SessionMetricChannel
            {
                ChannelId = id.Value,
                Name = name.Trim(),
                ColorHex = NormalizeColorHex(JsonUtil.TryGetString(channelElement, "color")),
                Unit = NormalizeOptionalString(JsonUtil.TryGetString(channelElement, "unit")),
                Type = NormalizeOptionalString(JsonUtil.TryGetString(channelElement, "type")) ?? "custom",
                Source = NormalizeOptionalString(JsonUtil.TryGetString(channelElement, "source")),
                Group = NormalizeOptionalString(JsonUtil.TryGetString(channelElement, "group")),
                Kind = NormalizeOptionalString(JsonUtil.TryGetString(channelElement, "kind"))
            });
        }

        return channels;
    }

    private static string? NormalizeOptionalString(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Trim();
    }

    private static IReadOnlyList<SessionMetricSample> ParseMetricSamples(JsonElement root)
    {
        if (!root.TryGetProperty("metrics", out var metricsElement) || metricsElement.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<SessionMetricSample>();
        }

        var samples = new List<SessionMetricSample>();
        foreach (var metricElement in metricsElement.EnumerateArray())
        {
            var channelId = TryReadByte(metricElement, "channel");
            var value = TryReadInt64(metricElement, "value");
            var capturedAtUtc = JsonUtil.TryGetString(metricElement, "capturedAtUtc");
            if (channelId is null || value is null)
            {
                continue;
            }

            var parsedTimestamp = DateTimeOffset.TryParse(capturedAtUtc, out var parsed)
                ? parsed
                : DateTimeOffset.UtcNow;

            samples.Add(new SessionMetricSample
            {
                ChannelId = channelId.Value,
                Value = value.Value,
                CapturedAtUtc = parsedTimestamp
            });
        }

        return samples;
    }

    private static IReadOnlyList<SessionApplicationEvent> ParseAppEvents(JsonElement root)
    {
        if (!root.TryGetProperty("events", out var eventsElement) || eventsElement.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<SessionApplicationEvent>();
        }

        var events = new List<SessionApplicationEvent>();
        foreach (var eventElement in eventsElement.EnumerateArray())
        {
            var id = JsonUtil.TryGetString(eventElement, "id");
            var label = JsonUtil.TryGetString(eventElement, "label");
            if (string.IsNullOrWhiteSpace(label))
            {
                continue;
            }

            var eventType = JsonUtil.TryGetString(eventElement, "eventType");
            var details = JsonUtil.TryGetString(eventElement, "details") ?? string.Empty;
            var channelId = TryReadByte(eventElement, "channel")
                            ?? TryReadByte(eventElement, "channelId")
                            ?? 0;
            var capturedAtUtc = JsonUtil.TryGetString(eventElement, "capturedAtUtc");
            var parsedTimestamp = DateTimeOffset.TryParse(capturedAtUtc, out var parsed)
                ? parsed
                : DateTimeOffset.UtcNow;

            events.Add(new SessionApplicationEvent(
                string.IsNullOrWhiteSpace(id) ? Guid.NewGuid().ToString("D") : id.Trim(),
                label.Trim(),
                string.IsNullOrWhiteSpace(eventType) ? "Event" : eventType.Trim(),
                details.Trim(),
                parsedTimestamp,
                channelId));
        }

        return events;
    }

    private static IReadOnlyList<SessionTouchInputRecord> ParseTouchInputRecords(JsonElement root)
    {
        var type = JsonUtil.TryGetString(root, "type");
        if (!string.Equals(type, WebSocketClientEventConstants.ClientTouchInput, StringComparison.Ordinal)
            || !string.Equals(JsonUtil.TryGetString(root, "schema"), SessionTouchPacking.SchemaName, StringComparison.Ordinal))
        {
            return Array.Empty<SessionTouchInputRecord>();
        }

        try
        {
            var batch = JsonSerializer.Deserialize<SessionTouchPackedBatch>(root.GetRawText(), JsonUtil.Compact);
            if (batch is null)
            {
                return Array.Empty<SessionTouchInputRecord>();
            }

            return SessionTouchPacking.Unpack([batch]);
        }
        catch
        {
            return Array.Empty<SessionTouchInputRecord>();
        }
    }

    private static byte? TryReadByte(JsonElement parent, string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetByte(out var numberValue))
        {
            return numberValue;
        }

        if (value.ValueKind == JsonValueKind.String && byte.TryParse(value.GetString(), out var stringValue))
        {
            return stringValue;
        }

        return null;
    }

    private static long? TryReadInt64(JsonElement parent, string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var numericValue))
        {
            return numericValue;
        }

        if (value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), out var stringValue))
        {
            return stringValue;
        }

        return null;
    }

    private static double? TryReadDouble(JsonElement parent, string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var numericValue))
        {
            return numericValue;
        }

        if (value.ValueKind == JsonValueKind.String
            && double.TryParse(
                value.GetString(),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var stringValue))
        {
            return stringValue;
        }

        return null;
    }

    private static string NormalizeColorHex(string? colorHex)
    {
        if (string.IsNullOrWhiteSpace(colorHex))
        {
            return "#4A90E2";
        }

        var trimmed = colorHex.Trim();
        if (trimmed.Length == 7 && trimmed[0] == '#')
        {
            return trimmed.ToUpperInvariant();
        }

        return "#4A90E2";
    }

    private static DeviceAppProfile? ParseDeviceAppProfile(string payload, JsonElement root)
    {
        var type = JsonUtil.TryGetString(root, "type");
        if (!string.Equals(type, WebSocketClientEventConstants.DeviceAppProfile, StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<DeviceAppProfile>(payload, JsonUtil.Compact);
        }
        catch
        {
            return null;
        }
    }

    private static string? PrettyPrintJson(string payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload, JsonUtil.Document);
            return JsonSerializer.Serialize(document.RootElement, JsonUtil.Pretty);
        }
        catch
        {
            return payload;
        }
    }

    private static ParsedSessionImageFrame? ParseSessionImageFrame(JsonElement root)
    {
        var type = JsonUtil.TryGetString(root, "type");
        if (!string.Equals(type, WebSocketClientEventConstants.ClientJpeg, StringComparison.Ordinal))
        {
            return null;
        }

        var base64 = JsonUtil.TryGetString(root, "base64");
        if (string.IsNullOrWhiteSpace(base64))
        {
            return null;
        }

        try
        {
            return new ParsedSessionImageFrame(
                CapturedAtUtc: ReadCapturedAtUtc(root),
                Format: JsonUtil.TryGetString(root, "format") ?? "jpeg",
                Width: TryReadInt32(root, "width") ?? 0,
                Height: TryReadInt32(root, "height") ?? 0,
                Quality: TryReadInt32(root, "quality") ?? 70,
                Bytes: Convert.FromBase64String(base64));
        }
        catch
        {
            return null;
        }
    }

    private static SessionVisualTreeSnapshot? ParseSessionVisualTreeSnapshot(JsonElement root)
    {
        var type = JsonUtil.TryGetString(root, "type");
        if (!string.Equals(type, WebSocketClientEventConstants.ClientVisualTree, StringComparison.Ordinal)
            || !root.TryGetProperty("payload", out var payloadElement)
            || payloadElement.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        try
        {
            var payload = JsonNode.Parse(
                payloadElement.GetRawText(),
                documentOptions: JsonUtil.Document) as JsonObject;
            if (payload is null || payload.Count == 0)
            {
                return null;
            }

            var capturedAtUtc = ReadCapturedAtUtc(root);
            var reportedKind = LiveUiNodeQuery.ReadString(payload, "treeKind")
                               ?? JsonUtil.TryGetString(root, "visualTreeKind");
            var source = LiveUiNodeQuery.ReadString(payload, "source");
            var format = JsonUtil.TryGetString(root, "visualTreeFormat")
                         ?? LiveUiNodeQuery.ReadString(payload, "format");
            return new SessionVisualTreeSnapshot
            {
                SnapshotId = JsonUtil.TryGetString(root, "snapshotId")?.Trim() is { Length: > 0 } snapshotId
                    ? snapshotId
                    : $"stream-{capturedAtUtc:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}",
                CapturedAtUtc = capturedAtUtc,
                VisualTreeKind = VisualTreeContract.NormalizeKind(
                    toolId: null,
                    reportedKind,
                    source,
                    format),
                VisualTreeFormat = VisualTreeContract.NormalizeFormat(format),
                RuntimePlatform = VisualTreeContract.NormalizeRuntimePlatform(
                    JsonUtil.TryGetString(root, "runtimePlatform")
                    ?? LiveUiNodeQuery.ReadString(payload, "platform")),
                Source = JsonUtil.TryGetString(root, "source") ?? "sdk.sessionCapture",
                RootScope = VisualTreeContract.NormalizeRootScope(
                    JsonUtil.TryGetString(root, "rootScope")
                    ?? LiveUiNodeQuery.ReadString(payload, "rootScope")),
                MaxDepth = Math.Max(0, TryReadInt32(root, "maxDepth") ?? 0),
                IncludeProperties = TryReadBoolean(root, "includeProperties") ?? false,
                IncludeBindableProperties = TryReadBoolean(root, "includeBindableProperties") ?? false,
                NodeCount = Math.Max(0, TryReadInt32(root, "nodeCount") ?? 0),
                Truncated = TryReadBoolean(root, "truncated") ?? false,
                ScreenshotCapturedAtUtc = ParseOptionalDateTimeOffset(root, "screenshotCapturedAtUtc"),
                Payload = payload
            };
        }
        catch
        {
            return null;
        }
    }

    private static DateTimeOffset ReadCapturedAtUtc(JsonElement root)
    {
        var capturedAtUtc = JsonUtil.TryGetString(root, "capturedAtUtc");
        return DateTimeOffset.TryParse(capturedAtUtc, out var parsed)
            ? parsed
            : DateTimeOffset.UtcNow;
    }

    private static DateTimeOffset? ParseOptionalDateTimeOffset(JsonElement root, string propertyName)
    {
        var value = JsonUtil.TryGetString(root, propertyName);
        return DateTimeOffset.TryParse(value, out var parsed)
            ? parsed
            : null;
    }

    private static AppLifecycleState? ParseAppLifecycleState(JsonElement root)
    {
        var type = JsonUtil.TryGetString(root, "type");
        if (!string.Equals(type, WebSocketClientEventConstants.ClientAppState, StringComparison.Ordinal))
        {
            return null;
        }

        var value = JsonUtil.TryGetString(root, "state");
        return value?.Trim().ToLowerInvariant() switch
        {
            "foreground" => AppLifecycleState.Foreground,
            "background" => AppLifecycleState.Background,
            "unknown" => AppLifecycleState.Unknown,
            _ => null
        };
    }

    private static int? TryReadInt32(JsonElement parent, string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var numericValue))
        {
            return numericValue;
        }

        if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out var stringValue))
        {
            return stringValue;
        }

        return null;
    }

    private static bool? TryReadBoolean(JsonElement parent, string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            return value.GetBoolean();
        }

        return value.ValueKind == JsonValueKind.String && bool.TryParse(value.GetString(), out var parsed)
            ? parsed
            : null;
    }
}
