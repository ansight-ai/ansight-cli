using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SkiaSharp;

namespace Ansight.Cli.Commands.Mcp;

internal sealed class ReplayBackend(
    Func<string[], CancellationToken, Task<JsonObject>> run,
    HttpClient http,
    Func<Uri, string?>? openBrowser = null)
{
    private readonly ConcurrentDictionary<string, JsonObject> replays = new();
    internal static JsonObject Json(object value) => JsonSerializer.SerializeToNode(value)!.AsObject();
    internal static string Text(JsonNode? node, string key) => node?[key]?.GetValue<string>() ?? "";
    internal static long Time(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture).ToUnixTimeMilliseconds();
    internal static JsonArray Items(JsonNode? node) => node?["items"] as JsonArray ?? [];
    private static JsonNode? Copy(JsonNode? node) => node?.DeepClone();

    internal static string Id(string value)
    {
        if (!Regex.IsMatch(value, @"\A[a-zA-Z0-9][a-zA-Z0-9_.-]{0,199}\z") || value.Contains("..", StringComparison.Ordinal))
            throw new ArgumentException("Invalid Ansight session, annotation, or frame identifier.");
        return value;
    }

    internal static Uri LocalHost(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "http"
            || uri.Host is not ("127.0.0.1" or "localhost" or "[::1]")
            || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
            throw new ArgumentException("Replay connects only to the local loopback Ansight host.");
        return new Uri(value.TrimEnd('/') + "/");
    }

    internal async Task<JsonObject> ListAsync(JsonObject input, CancellationToken token)
    {
        var args = new List<string> { "session", "list", "--limit", (input["limit"]?.GetValue<int>() ?? 10).ToString(CultureInfo.InvariantCulture) };
        foreach (var field in new[] { "search", "cursor" })
            if (input[field] is not null) args.AddRange([$"--{field}", Text(input, field)]);
        var found = await run(args.ToArray(), token);
        var sessions = new JsonArray();
        foreach (var item in found["sessions"]?.AsArray() ?? [])
            sessions.Add(Json(new { sessionId = Text(item, "sessionId"), title = Title(item),
                appName = Text(item, "clientName") is { Length: > 0 } name ? name : Text(item, "appId"),
                createdUtc = Text(item, "createdUtc"), lastUpdatedUtc = Text(item, "lastUpdatedUtc") }));
        return new JsonObject { ["sessions"] = sessions, ["nextCursor"] = Copy(found["nextCursor"]) };
    }

    private static string Title(JsonNode? item) => new[] { "name", "clientName", "sessionId" }
        .Select(key => Text(item, key)).FirstOrDefault(value => value.Length > 0) ?? "Ansight replay";

    internal async Task<JsonObject> LoadAsync(string sessionId, CancellationToken token)
    {
        Id(sessionId);
        var found = await run(["session", "list", "--search", sessionId, "--limit", "20"], token);
        var summary = found["sessions"]?.AsArray().FirstOrDefault(item => Text(item, "sessionId") == sessionId)
            ?? throw new ArgumentException("Local session was not found.");
        var evidence = await Task.WhenAll(
            run(["session", "images", sessionId, "--limit", "10000"], token),
            run(["session", "annotations", sessionId, "--limit", "10000"], token),
            run(["session", "touches", sessionId, "--limit", "10000"], token),
            run(["host", "status"], token));
        var replay = MakeReplay(summary, evidence[0], evidence[1], evidence[2]);
        var host = Host(evidence[3]);
        replay["fullReplayUrl"] = new UriBuilder(host) { Query = "sessionId=" + Uri.EscapeDataString(sessionId) }.Uri.ToString();
        if (replays.Count >= 12) replays.TryRemove(replays.Keys.First(), out _);
        replays[sessionId] = replay;
        return replay.DeepClone().AsObject();
    }

    private static Uri Host(JsonObject status)
    {
        if (status["isRunning"]?.GetValue<bool>() != true || Text(status, "explorerUrl").Length == 0)
            throw new InvalidOperationException("Start the local Ansight host before loading a replay.");
        return LocalHost(Text(status, "explorerUrl"));
    }

    internal async Task<JsonObject> OpenInBrowserAsync(string sessionId, CancellationToken token)
    {
        Id(sessionId);
        var found = await run(["session", "list", "--search", sessionId, "--limit", "20"], token);
        if (found["sessions"]?.AsArray().Any(item => Text(item, "sessionId") == sessionId) != true)
            throw new ArgumentException("Local session was not found.");
        // Resolve the current host address locally; the widget never supplies a URL.
        var host = Host(await run(["host", "status"], token));
        var url = new UriBuilder(host) { Query = "sessionId=" + Uri.EscapeDataString(sessionId) }.Uri;
        token.ThrowIfCancellationRequested();
        var warning = (openBrowser ?? CliBrowserLauncher.TryOpen)(url);
        if (warning is not null) throw new InvalidOperationException($"Could not open the local host player: {warning}");
        return Json(new { sessionId, replayUrl = url.AbsoluteUri });
    }

    internal static JsonObject MakeReplay(JsonNode summary, JsonObject images, JsonObject annotations, JsonObject touches)
    {
        var frames = new JsonArray();
        foreach (var frame in Items(images).Where(HasTime).OrderBy(item => Time(Text(item, "capturedAtUtc"))))
            frames.Add(new JsonObject { ["frameId"] = Id(Text(frame, "frameId")), ["capturedAtUtc"] = Copy(frame!["capturedAtUtc"]),
                ["timeMs"] = Time(Text(frame, "capturedAtUtc")), ["width"] = Copy(frame["width"]), ["height"] = Copy(frame["height"]) });
        if (frames.Count == 0) throw new ArgumentException("This session has no recorded screenshots to replay.");
        if ((images["totalCount"]?.GetValue<int>() ?? frames.Count) > frames.Count || frames.Count > 10000)
            throw new ArgumentException("Inline replay supports up to 10,000 screenshots. Select a shorter capture.");
        var samples = new JsonArray();
        foreach (var touch in Items(touches).Where(HasTime).OrderBy(item => Time(Text(item, "capturedAtUtc"))))
        {
            var sample = new JsonObject { ["timeMs"] = Time(Text(touch, "capturedAtUtc")) };
            foreach (var field in new[] { "id", "action", "pointerId", "pointerIndex", "x", "y", "normalizedX", "normalizedY", "surfaceWidth", "surfaceHeight", "coordinateUnit" })
                sample[field] = Copy(touch![field]);
            sample["details"] = Copy(touch?["details"]);
            sample["tool"] = Copy(touch?["details"]?["tool"]);
            sample["sampleKind"] = Copy(touch?["details"]?["sampleKind"]);
            samples.Add(sample);
        }
        var start = frames[0]!["timeMs"]!.GetValue<long>();
        var end = Math.Max(frames[^1]!["timeMs"]!.GetValue<long>(),
            DateTimeOffset.TryParse(Text(summary, "lastUpdatedUtc"), out var updated) ? updated.ToUnixTimeMilliseconds() : start);
        if (samples.Count > 0) end = Math.Max(end, samples[^1]!["timeMs"]!.GetValue<long>() + 1600);
        var markers = new JsonArray();
        foreach (var annotation in Items(annotations).Where(item => DateTimeOffset.TryParse(Text(item, "startUtc"), out _)).OrderBy(item => Time(Text(item, "startUtc"))))
            markers.Add(Json(new { id = Text(annotation, "annotationId"), label = Text(annotation, "label"), notes = Text(annotation, "notes"),
                timeMs = Math.Clamp(Time(Text(annotation, "startUtc")), start, end), capturedAtUtc = Text(annotation, "startUtc") }));
        var total = touches["totalCount"]?.GetValue<int>() ?? samples.Count;
        return new JsonObject { ["sessionId"] = Text(summary, "sessionId"), ["title"] = Title(summary), ["appName"] = Text(summary, "clientName"),
            ["startMs"] = start, ["endMs"] = end, ["frames"] = frames, ["markers"] = markers, ["touches"] = samples,
            ["annotations"] = Items(annotations).DeepClone(), ["touchTotalCount"] = total,
            ["touchWarning"] = total > samples.Count ? $"Showing {samples.Count} of {total} touch samples. Open the full replay for all touches." : null };
    }

    private static bool HasTime(JsonNode? node) => DateTimeOffset.TryParse(Text(node, "capturedAtUtc"), out _);

    internal async Task<JsonArray> FramesAsync(string sessionId, string[] frameIds, CancellationToken token)
    {
        Id(sessionId);
        if (frameIds.Length is < 1 or > 6) throw new ArgumentException("Request 1–6 frames.");
        var replay = replays.TryGetValue(sessionId, out var cached) ? cached : await LoadAsync(sessionId, token);
        var allowed = replay["frames"]!.AsArray().Select(frame => Text(frame, "frameId")).ToHashSet(StringComparer.Ordinal);
        foreach (var id in frameIds)
            if (!allowed.Contains(Id(id))) throw new ArgumentException("Frame does not belong to this replay.");
        var host = Host(await run(["host", "status"], token));
        var frames = new JsonArray();
        foreach (var frameId in frameIds)
        {
            using var response = await http.GetAsync(new Uri(host, $"api/sessions/{Uri.EscapeDataString(sessionId)}/frames/{Uri.EscapeDataString(frameId)}"), HttpCompletionOption.ResponseHeadersRead, token);
            response.EnsureSuccessStatusCode();
            var mime = response.Content.Headers.ContentType?.MediaType;
            if (mime is not ("image/png" or "image/webp" or "image/jpeg")) throw new ArgumentException("Unsupported screenshot format.");
            await using var stream = await response.Content.ReadAsStreamAsync(token);
            using var buffer = new MemoryStream();
            var chunk = new byte[65536];
            int count;
            while ((count = await stream.ReadAsync(chunk, token)) > 0)
            {
                if (buffer.Length + count > 32 * 1024 * 1024) throw new ArgumentException("Screenshot exceeds the 32 MiB source limit.");
                buffer.Write(chunk, 0, count);
            }
            var bytes = buffer.ToArray();
            if (bytes.Length > 256 * 1024)
            {
                using var data = SKData.CreateCopy(bytes);
                using var codec = SKCodec.Create(data) ?? throw new ArgumentException("Screenshot could not be decoded.");
                if ((long)codec.Info.Width * codec.Info.Height > 40000000) throw new ArgumentException("Screenshot exceeds the pixel limit.");
                using var bitmap = SKBitmap.Decode(codec);
                var scale = Math.Min(1d, Math.Min(960d / bitmap.Width, 1440d / bitmap.Height));
                using var resized = bitmap.Resize(new SKImageInfo(Math.Max(1, (int)(bitmap.Width * scale)), Math.Max(1, (int)(bitmap.Height * scale))), new SKSamplingOptions(SKFilterMode.Linear));
                using var image = SKImage.FromBitmap(resized);
                using var encoded = image.Encode(SKEncodedImageFormat.Webp, 82);
                bytes = encoded.ToArray();
                mime = "image/webp";
            }
            if (bytes.Length > 2 * 1024 * 1024) throw new ArgumentException("Display screenshot exceeds the 2 MiB limit.");
            frames.Add(Json(new { frameId, dataUrl = $"data:{mime};base64,{Convert.ToBase64String(bytes)}" }));
        }
        return frames;
    }

    internal async Task<JsonObject> CreateAnnotationAsync(JsonObject input, CancellationToken token)
    {
        var sessionId = Id(Text(input, "sessionId"));
        var annotationId = Id(Text(input, "annotationId"));
        var frameId = Id(Text(input, "frameId"));
        var replay = await LoadAsync(sessionId, token);
        var frame = replay["frames"]!.AsArray().FirstOrDefault(item => Text(item, "frameId") == frameId)
            ?? throw new ArgumentException("Annotation frame does not belong to this replay.");
        var geometry = input["geometry"]!;
        var kind = geometry["kind"]!.GetValue<int>();
        var x = geometry["x"]!.GetValue<double>();
        var y = geometry["y"]!.GetValue<double>();
        var width = geometry["width"]?.GetValue<double>() ?? 0;
        var height = geometry["height"]?.GetValue<double>() ?? 0;
        if (x + width > 1 || y + height > 1 || kind == 1 && (width <= 0 || height <= 0))
            throw new ArgumentException("Annotation geometry must stay within the screenshot.");
        var comment = Text(input, "comment").Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
        if (comment.Length == 0) throw new ArgumentException("An annotation comment is required.");
        var lines = comment.Split('\n');
        var notes = string.Join('\n', lines.Skip(1)).Trim();
        var status = Text(input, "status").Trim();
        var shape = new JsonObject { ["geometryId"] = annotationId + "-shape", ["frameId"] = frameId,
            ["capturedAtUtc"] = Text(frame, "capturedAtUtc"), ["kind"] = kind, ["x"] = x, ["y"] = y,
            ["width"] = kind == 1 ? width : null, ["height"] = kind == 1 ? height : null, ["strokeColor"] = "#FFFA441F", ["strokeWidth"] = 3 };
        var annotation = new JsonObject { ["annotationId"] = annotationId, ["captureGroupId"] = annotationId,
            ["startUtc"] = Text(frame, "capturedAtUtc"), ["endUtc"] = DateTimeOffset.FromUnixTimeMilliseconds(frame["timeMs"]!.GetValue<long>() + 2000).ToString("O"),
            ["label"] = lines[0].Trim(), ["notes"] = notes.Length > 0 ? notes : null, ["status"] = status.Length > 0 ? status : null,
            ["source"] = "mcp.replay.screenshot", ["customData"] = null, ["target"] = null, ["geometry"] = new JsonArray(shape) };
        var existing = replay["annotations"]!.AsArray().FirstOrDefault(item => Text(item, "annotationId") == annotationId);
        if (existing is not null)
        {
            if (new[] { "source", "label", "notes", "status" }.All(key => JsonNode.DeepEquals(existing[key], annotation[key]))
                && new[] { "frameId", "kind", "x", "y", "width", "height" }.All(key => JsonNode.DeepEquals(existing["geometry"]?[0]?[key], shape[key])))
                return existing.DeepClone().AsObject();
            throw new ArgumentException("That annotation ID already exists. Existing annotations cannot be replaced by this tool.");
        }
        var host = Host(await run(["host", "status"], token));
        using var response = await http.PostAsJsonAsync(new Uri(host, $"api/sessions/{Uri.EscapeDataString(sessionId)}/annotations"), annotation, token);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<JsonObject>(token);
        if (result?["isSuccess"]?.GetValue<bool>() != true) throw new InvalidOperationException(Text(result, "message"));
        var saved = await run(["session", "annotations", sessionId, "--limit", "10000"], token);
        return Items(saved).FirstOrDefault(item => Text(item, "annotationId") == annotationId)?.DeepClone().AsObject()
            ?? throw new InvalidOperationException("Annotation was accepted but could not be read back. Refresh before retrying.");
    }
}
