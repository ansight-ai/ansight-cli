using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ansight.Host.Connectivity;

public static class SessionConnectivity
{
    public static bool IsConnectivity(SessionApplicationEvent value) => value.Label.StartsWith("connectivity.", StringComparison.Ordinal);

    public static IReadOnlyList<ConnectivityRecord> Read(IEnumerable<SessionApplicationEvent> events)
        => events.Where(IsConnectivity).Select(value => ConnectivityJson.Parse(value.Details))
            .OfType<ConnectivityRecord>().DistinctBy(value => value.Id)
            .OrderBy(value => value.CapturedAtUtc).ThenBy(value => value.Sequence).ThenBy(value => value.Id, StringComparer.Ordinal).ToArray();

    public static SessionApplicationEvent ToEvent(ConnectivityRecord record)
        => new(record.Id, $"connectivity.{record.Kind}", "Info", ConnectivityJson.Serialize(record), record.CapturedAtUtc, 0);

    public static SessionApplicationEvent ShiftEvent(SessionApplicationEvent value, DateTimeOffset timestamp)
    {
        if (!IsConnectivity(value) || ConnectivityJson.Parse(value.Details) is not { } record)
            return value with { CapturedAtUtc = timestamp };
        var delta = timestamp - value.CapturedAtUtc;
        return ToEvent(record with
        {
            OriginalCapturedAtUtc = record.OriginalCapturedAtUtc ?? record.CapturedAtUtc,
            CapturedAtUtc = timestamp,
            Quality = record.Quality is { } quality ? quality with
            {
                OriginalMeasuredAtUtc = quality.OriginalMeasuredAtUtc ?? quality.MeasuredAtUtc,
                MeasuredAtUtc = quality.MeasuredAtUtc + delta
            } : null
        });
    }

    public static IReadOnlyList<SessionApplicationEvent> Extract(IEnumerable<SessionApplicationEvent> events, DateTimeOffset from, DateTimeOffset to)
    {
        var all = events.ToArray();
        var baseline = Read(all).LastOrDefault(value => value.CapturedAtUtc < from);
        return all.Where(value => value.CapturedAtUtc >= from && value.CapturedAtUtc <= to)
            .Concat(baseline is null ? [] : new[] { ToEvent(baseline) }).DistinctBy(value => value.EventId)
            .OrderBy(value => value.CapturedAtUtc).ToArray();
    }

    public static DateTimeOffset? ParseTime(string? value, DateTimeOffset origin)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (value.Contains('T') && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var timestamp)) return timestamp.ToUniversalTime();
        var parts = value.Split(':');
        if (parts.Length is 2 or 3 && parts.All(part => double.TryParse(part, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number) && number >= 0))
        {
            var numbers = parts.Select(part => double.Parse(part, CultureInfo.InvariantCulture)).ToArray();
            if (numbers[^1] < 60 && (parts.Length == 2 || numbers[1] < 60))
            {
                var seconds = parts.Length == 2 ? numbers[0] * 60 + numbers[1] : numbers[0] * 3600 + numbers[1] * 60 + numbers[2];
                if (seconds < 31_536_000) return origin.AddSeconds(seconds);
            }
        }
        throw new ArgumentException("Time must be an ISO-8601 timestamp or a non-negative mm:ss / hh:mm:ss session offset.");
    }

    public static JsonObject Query(AppSessionSnapshot session, string mode = "summary", DateTimeOffset? from = null,
        DateTimeOffset? to = null, DateTimeOffset? at = null, string? kind = null, int limit = 200, string? cursor = null)
    {
        if (limit is < 1 or > 1000) throw new ArgumentException("limit must be between 1 and 1000.");
        if (mode is not ("summary" or "events" or "quality" or "timeline" or "at")) throw new ArgumentException("Unknown connectivity query mode.");
        if (kind is not null && kind is not ("snapshot" or "change" or "quality" or "gap")) throw new ArgumentException("Invalid connectivity kind.");
        if (from > to) throw new ArgumentException("from must not be after to.");
        var records = Read(session.ApplicationEvents);
        var origin = session.CreatedUtc;
        var end = session.LastUpdatedUtc < origin ? origin : session.LastUpdatedUtc;
        var requestedStart = from ?? origin;
        var requestedFinish = to ?? end;
        var start = requestedStart < origin ? origin : requestedStart > end ? end : requestedStart;
        var finish = requestedFinish > end ? end : requestedFinish < origin ? origin : requestedFinish;
        if (finish < start) finish = start;
        var payload = new JsonObject
        {
            ["schema"] = "ansight.session-connectivity.v1", ["sessionId"] = session.SessionId,
            ["captured"] = records.Count > 0, ["recordCount"] = records.Count,
            ["malformedRecordCount"] = session.ApplicationEvents.Count(value => IsConnectivity(value) && ConnectivityJson.Parse(value.Details) is null),
            ["fromUtc"] = start, ["toUtc"] = finish,
            ["captureFromUtc"] = origin, ["captureToUtc"] = end,
            ["rangeClipped"] = requestedStart != start || requestedFinish != finish,
            ["supportedMeasurements"] = JsonSerializer.SerializeToNode(records.SelectMany(value => value.SupportedMeasurements).Distinct().Order().ToArray())
        };
        if (mode == "at")
        {
            if (at is null) throw new ArgumentException("at is required.");
            payload["atUtc"] = at;
            var record = at < origin || at > end ? null : records.LastOrDefault(value => value.CapturedAtUtc <= at);
            payload["record"] = JsonSerializer.SerializeToNode(record, ConnectivityJson.Options);
            payload["availability"] = record?.Snapshot.Availability ?? "unknown";
            payload["measurementAgeMilliseconds"] = record?.Quality is { } quality ? Math.Max(0, (at.Value - quality.MeasuredAtUtc).TotalMilliseconds) : null;
            return payload;
        }
        var baseline = records.LastOrDefault(value => value.CapturedAtUtc < start);
        var window = records.Where(value => value.CapturedAtUtc >= start && value.CapturedAtUtc <= finish).ToArray();
        payload["precedingRecord"] = JsonSerializer.SerializeToNode(baseline, ConnectivityJson.Options);
        if (mode == "summary")
        {
            var durations = new Dictionary<string, double> { ["available"] = 0, ["unavailable"] = 0, ["unknown"] = 0 };
            var state = baseline?.Snapshot.Availability ?? "unknown";
            var position = start;
            foreach (var record in window)
            {
                durations[state] += Math.Max(0, (record.CapturedAtUtc - position).TotalMilliseconds);
                state = record.Snapshot.Availability;
                position = record.CapturedAtUtc;
            }
            durations[state] += Math.Max(0, (finish - position).TotalMilliseconds);
            payload["availabilityDurationMilliseconds"] = JsonSerializer.SerializeToNode(durations);
            payload["changeCount"] = window.Count(value => value.Kind == "change");
            payload["disconnectionCount"] = window.Count(value => value.Kind == "change" && value.PreviousSnapshot?.Availability == "available" && value.Snapshot.Availability == "unavailable");
            payload["reconnectionCount"] = window.Count(value => value.Kind == "change" && value.PreviousSnapshot?.Availability == "unavailable" && value.Snapshot.Availability == "available");
            payload["gapCount"] = window.Count(value => value.Kind == "gap");
            payload["qualitySampleCount"] = window.Count(value => value.Quality is not null);
            payload["transports"] = JsonSerializer.SerializeToNode(window.SelectMany(value => value.Snapshot.Transports).Concat(baseline?.Snapshot.Transports ?? []).Distinct().Order().ToArray());
            return payload;
        }
        var matches = window.Where(value => (kind is null || value.Kind == kind)
            && (mode != "events" || value.Kind != "quality") && (mode != "quality" || value.Quality is not null)).ToArray();
        // Bind cursors to the session, filters and exact result. Live changes require a fresh query.
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            string.Join('|', new[] { session.SessionId, mode, start.ToString("O"), finish.ToString("O"), kind ?? "" }.Concat(matches.Select(value => value.Id))))))[..24];
        var offset = 0;
        if (cursor is not null)
        {
            var split = cursor.Split(':');
            if (split.Length != 2 || split[0] != fingerprint || !int.TryParse(split[1], NumberStyles.None, CultureInfo.InvariantCulture, out offset) || offset < 0 || offset >= matches.Length)
                throw new ArgumentException("Connectivity cursor is invalid or the capture/filters changed. Start a new query.");
        }
        var page = new JsonArray();
        var characters = payload.ToJsonString().Length;
        foreach (var record in matches.Skip(offset).Take(limit))
        {
            var json = ConnectivityJson.Serialize(record);
            if (characters + json.Length > 400_000) break;
            page.Add(JsonNode.Parse(json)); characters += json.Length;
        }
        var next = offset + page.Count;
        payload["records"] = page;
        payload["matchedRecordCount"] = matches.Length;
        payload["returnedRecordCount"] = page.Count;
        payload["hasMore"] = next < matches.Length;
        payload["nextCursor"] = next < matches.Length ? $"{fingerprint}:{next}" : null;
        return payload;
    }
}
