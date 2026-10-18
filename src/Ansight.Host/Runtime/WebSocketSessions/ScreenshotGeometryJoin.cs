using System.Text.Json.Nodes;

namespace Ansight.Host.Runtime.WebSocketSessions;

/// <summary>One bounded join per WebSocket connection. Never associate by nearest timestamp.</summary>
internal sealed class ScreenshotGeometryJoin(Action<string, JsonObject?> updateFrame, Func<DateTimeOffset>? clock = null)
{
    private const int MaximumEntries = 128;
    private readonly Dictionary<FrameKey, Entry> entries = [];
    private readonly Func<DateTimeOffset> now = clock ?? (() => DateTimeOffset.UtcNow);
    private long sequence;

    internal bool TryAcceptMetadata(string text)
    {
        if (text.Length > 32 * 1024) return false;
        JsonObject? message;
        try { message = JsonNode.Parse(text) as JsonObject; }
        catch (JsonException) { return false; }
        if (message?["type"]?.ToString() != "CLIENT_SCREENSHOT_GEOMETRY") return false;
        try
        {
            var timestamp = message["capturedAtEpochMs"]!.GetValue<long>();
            var width = message["width"]!.GetValue<int>();
            var height = message["height"]!.GetValue<int>();
            var hash = message["sha256"]!.GetValue<string>();
            if (width <= 0 || height <= 0 || (long)width * height > 16 * 1024 * 1024
                || hash.Length != 64 || !hash.All(char.IsAsciiHexDigit)) return true;
            _ = DateTimeOffset.FromUnixTimeMilliseconds(timestamp);
            var entry = Get(new(timestamp, width, height, hash.ToLowerInvariant()));
            var geometry = ScreenshotGeometry.Validate(message["geometry"]);
            if (geometry is null || (entry.HasMetadata && !JsonNode.DeepEquals(entry.Geometry, geometry))) entry.Conflicted = true;
            entry.HasMetadata = true;
            entry.Geometry = entry.Conflicted ? null : geometry;
            foreach (var id in entry.FrameIds) updateFrame(id, entry.Geometry);
        }
        catch (Exception error) when (error is InvalidOperationException or FormatException or ArgumentException or NullReferenceException) { }
        return true;
    }

    internal JsonObject? BeforeFrame(ParsedSessionImageFrame image) => Get(Key(image)).Geometry?.DeepClone().AsObject();

    internal void AfterFrame(ParsedSessionImageFrame image, string frameId)
    {
        var entry = Get(Key(image));
        if (!entry.FrameIds.Contains(frameId)) entry.FrameIds.Add(frameId);
        // Two captures with the exact same key cannot be disambiguated safely.
        if (entry.FrameIds.Count > 1)
        {
            entry.Conflicted = true;
            entry.Geometry = null;
        }
        foreach (var id in entry.FrameIds) updateFrame(id, entry.Geometry);
    }

    private Entry Get(FrameKey key)
    {
        var current = now();
        foreach (var expired in entries.Where(pair => current - pair.Value.CreatedAt > TimeSpan.FromSeconds(30)).Select(pair => pair.Key).ToArray())
            entries.Remove(expired);
        if (entries.TryGetValue(key, out var found)) return found;
        if (entries.Count >= MaximumEntries) entries.Remove(entries.MinBy(pair => pair.Value.Sequence).Key);
        var entry = new Entry(current, sequence++);
        entries.Add(key, entry);
        return entry;
    }

    private static FrameKey Key(ParsedSessionImageFrame image) => new(image.CapturedAtUtc.ToUnixTimeMilliseconds(),
        image.Width, image.Height, Convert.ToHexStringLower(SHA256.HashData(image.Bytes.Span)));
    private sealed record FrameKey(long Timestamp, int Width, int Height, string Sha256);
    private sealed class Entry(DateTimeOffset createdAt, long sequence)
    {
        internal DateTimeOffset CreatedAt { get; } = createdAt;
        internal long Sequence { get; } = sequence;
        internal bool HasMetadata { get; set; }
        internal bool Conflicted { get; set; }
        internal JsonObject? Geometry { get; set; }
        internal List<string> FrameIds { get; } = [];
    }
}
