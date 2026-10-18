using System.Text.Json;

namespace Ansight.Host.Telemetry;

/// <summary>A timed operation projected from the existing durable application event stream.</summary>
public sealed record SessionSpan(
    string SpanId, string? ParentSpanId, string Name, byte ChannelId,
    DateTimeOffset StartedAtUtc, double? DurationMs, string Status)
{
    public static IReadOnlyList<SessionSpan> FromEvents(IEnumerable<SessionApplicationEvent> events)
    {
        var spans = new Dictionary<string, SessionSpan>(StringComparer.Ordinal);
        foreach (var item in events)
        {
            if (!string.Equals(item.EventType, "Span", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(item.Details)) continue;
            try
            {
                using var document = JsonDocument.Parse(item.Details);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object) continue;
                var id = ReadString(root, "spanId");
                var phase = ReadString(root, "phase");
                if (string.IsNullOrWhiteSpace(id) || phase is not ("start" or "end")) continue;
                if (!DateTimeOffset.TryParse(ReadString(root, "startedAtUtc"),
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal, out var started)) continue;
                double? duration = null;
                var status = "incomplete";
                if (phase == "end")
                {
                    if (!root.TryGetProperty("durationMs", out var value) || value.ValueKind != JsonValueKind.Number
                        || !value.TryGetDouble(out var elapsed) || !double.IsFinite(elapsed) || elapsed < 0
                        || elapsed > (DateTimeOffset.MaxValue - started).TotalMilliseconds) continue;
                    status = ReadString(root, "status") ?? "ok";
                    if (status is not ("ok" or "error" or "cancelled")) continue;
                    duration = elapsed;
                }
                // End records are self-contained and win over duplicate or out-of-order starts.
                if (spans.TryGetValue(id, out var existing) && existing.DurationMs is not null) continue;
                spans[id] = new SessionSpan(id, ReadString(root, "parentSpanId"), item.Label,
                    item.ChannelId, started.ToUniversalTime(), duration, status);
            }
            catch (JsonException) { /* Malformed app details remain available as ordinary events. */ }
        }
        return spans.Values.OrderBy(span => span.StartedAtUtc).ThenBy(span => span.SpanId, StringComparer.Ordinal).ToArray();
    }

    private static string? ReadString(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
