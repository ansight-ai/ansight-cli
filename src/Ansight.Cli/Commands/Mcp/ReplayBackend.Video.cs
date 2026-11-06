using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace Ansight.Cli.Commands.Mcp;

internal sealed partial class ReplayBackend
{
    internal const int VideoChunkBytes = 1024 * 1024;
    internal const long MaximumInlineVideoBytes = 128L * 1024 * 1024;

    private async Task<JsonArray> LoadRecordingsAsync(Uri host, string sessionId, CancellationToken token)
    {
        var snapshot = await http.GetFromJsonAsync<JsonObject>(
            new Uri(host, $"api/sessions/{Uri.EscapeDataString(sessionId)}"), token);
        var recordings = new JsonArray();

        foreach (var recording in snapshot?["videoRecordings"]?.AsArray() ?? [])
        {
            var durationUs = recording?["durationUs"]?.GetValue<long>() ?? 0;
            var byteSize = recording?["byteSize"]?.GetValue<long>() ?? 0;
            var width = recording?["width"]?.GetValue<int>() ?? 0;
            var height = recording?["height"]?.GetValue<int>() ?? 0;
            var hasValidMetadata = Text(recording, "status") == "completed"
                && DateTimeOffset.TryParse(Text(recording, "startedUtc"), out _)
                && durationUs > 0
                && byteSize > 0
                && width > 0
                && height > 0;

            if (!hasValidMetadata)
            {
                continue;
            }

            var startMs = Time(Text(recording, "startedUtc"));

            recordings.Add(Json(new
            {
                recordingId = Id(Text(recording, "recordingId")),
                startMs,
                endMs = checked(startMs + (durationUs + 999) / 1000),
                byteSize,
                width,
                height
            }));
        }

        return recordings;
    }

    internal async Task<JsonObject> VideoChunkAsync(string sessionId, string recordingId, long offset, CancellationToken token)
    {
        Id(sessionId);
        Id(recordingId);

        var replay = replays.TryGetValue(sessionId, out var cached) ? cached : await LoadAsync(sessionId, token);
        var recording = replay["recordings"]!.AsArray()
            .FirstOrDefault(item => Text(item, "recordingId") == recordingId)
            ?? throw new ArgumentException("Video does not belong to this replay.");
        var byteSize = recording["byteSize"]!.GetValue<long>();

        if (byteSize > MaximumInlineVideoBytes)
        {
            throw new ArgumentException("This recording exceeds the inline video limit. Open the full replay to watch it.");
        }

        if (offset < 0 || offset >= byteSize)
        {
            throw new ArgumentException("Video offset is outside the recording.");
        }

        var length = (int)Math.Min(VideoChunkBytes, byteSize - offset);
        var host = Host(await run(["host", "status"], token));
        using var request = new HttpRequestMessage(HttpMethod.Get,
            new Uri(host, $"api/sessions/{Uri.EscapeDataString(sessionId)}/video/{Uri.EscapeDataString(recordingId)}"));
        request.Headers.Range = new RangeHeaderValue(offset, offset + length - 1);

        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();

        var range = response.Content.Headers.ContentRange;
        var matchesRequestedRange = response.StatusCode == HttpStatusCode.PartialContent
            && response.Content.Headers.ContentType?.MediaType == "video/mp4"
            && range?.From == offset
            && range.To == offset + length - 1
            && range.Length == byteSize;

        if (!matchesRequestedRange)
        {
            throw new InvalidDataException("The host returned an unexpected video range.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(token);
        var bytes = new byte[length];
        await stream.ReadExactlyAsync(bytes, token);

        if (await stream.ReadAsync(new byte[1], token) != 0)
        {
            throw new InvalidDataException("The video response exceeded the requested range.");
        }

        return Json(new
        {
            recordingId,
            offset,
            byteSize,
            data = Convert.ToBase64String(bytes)
        });
    }
}
