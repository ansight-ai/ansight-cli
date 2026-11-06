using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Ansight.Cli.Commands.Mcp;

namespace Ansight.Cli.Tests.Commands.Mcp;

public sealed class ReplayMcpTests
{
    private static JsonObject Parse(string value) => JsonNode.Parse(value)!.AsObject();

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public JsonArray Annotations { get; } = JsonNode.Parse("""
            [{"annotationId":"marker-1","label":"Search result","startUtc":"2026-10-11T00:00:12Z"}]
            """)!.AsArray();
        public int Writes { get; private set; }
        public JsonArray Recordings { get; } = [];
        public byte[] VideoBytes { get; set; } = [0, 0, 0, 1, 2, 3];
        public bool ReturnInvalidRange { get; set; }
        public int VideoReads { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("127.0.0.1", request.RequestUri!.Host);

            if (request.Method == HttpMethod.Post)
            {
                Writes++;
                Annotations.Add(JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken)));

                return new(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"isSuccess\":true}", Encoding.UTF8, "application/json")
                };
            }

            if (request.RequestUri.AbsolutePath.EndsWith("/capture-1", StringComparison.Ordinal))
            {
                return new(HttpStatusCode.OK)
                {
                    Content = new StringContent(new JsonObject { ["videoRecordings"] = Recordings.DeepClone() }.ToJsonString(), Encoding.UTF8, "application/json")
                };
            }

            if (request.RequestUri.AbsolutePath.Contains("/video/", StringComparison.Ordinal))
            {
                VideoReads++;
                var range = Assert.Single(request.Headers.Range!.Ranges);
                var start = (int)range.From!.Value;
                var end = (int)range.To!.Value;
                var content = new ByteArrayContent(VideoBytes[start..(end + 1)]);
                content.Headers.ContentType = new("video/mp4");
                content.Headers.ContentRange = new(start, end, VideoBytes.Length);

                return new(ReturnInvalidRange ? HttpStatusCode.OK : HttpStatusCode.PartialContent) { Content = content };
            }

            return new(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([1, 2, 3]) { Headers = { ContentType = new("image/webp") } }
            };
        }

        public Task<JsonObject> Run(string[] args, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();

            var result = args[0] == "host" ? Parse("""{"isRunning":true,"explorerUrl":"http://127.0.0.1:47231/ansight/"}""") : args[1] switch
            {
                "list" => Parse("""{"sessions":[{"sessionId":"capture-1","name":"Search","clientName":"Test app","createdUtc":"2026-10-11T00:00:00Z","lastUpdatedUtc":"2026-10-11T00:00:30Z"}]}"""),
                "images" => Parse("""{"items":[{"frameId":"frame-b","capturedAtUtc":"2026-10-11T00:00:20Z","width":390,"height":844},{"frameId":"frame-a","capturedAtUtc":"2026-10-11T00:00:10Z","width":390,"height":844}]}"""),
                "touches" => Parse("""{"totalCount":2,"items":[{"id":"down","action":"down","capturedAtUtc":"2026-10-11T00:00:12Z","normalizedX":0.2,"normalizedY":0.7},{"id":"up","action":"up","capturedAtUtc":"2026-10-11T00:00:12.1Z"}]}"""),
                "annotations" => new JsonObject { ["items"] = Annotations.DeepClone() },
                _ => throw new InvalidOperationException("Unexpected command.")
            };

            return Task.FromResult(result);
        }
    }

    [Fact]
    public async Task ReplayReturnsTouchesAndPrivatePixelsWithAnExactFullReplayLink()
    {
        using var handler = new RecordingHandler();
        using var http = new HttpClient(handler);
        var command = new ReplayMcpCommand("<html>touch-overlay</html>", new ReplayBackend(handler.Run, http));
        var result = await command.InvokeAsync("show_replay", Parse("""{"sessionId":"capture-1","annotationId":"marker-1"}"""), default);
        Assert.Null(result["isError"]);
        Assert.Equal(2, result["structuredContent"]!["touchCount"]!.GetValue<int>());
        Assert.Equal(command.ViewerBuild, result["structuredContent"]!["viewerBuild"]!.GetValue<string>());
        Assert.DoesNotContain("base64", result["structuredContent"]!.ToJsonString());
        Assert.Equal("data:image/webp;base64,AQID", result["_meta"]!["frames"]![0]!["dataUrl"]!.GetValue<string>());
        Assert.Equal("frame-a", result["_meta"]!["frames"]![0]!["frameId"]!.GetValue<string>());
        Assert.Equal("http://127.0.0.1:47231/ansight/?sessionId=capture-1", result["_meta"]!["replay"]!["fullReplayUrl"]!.GetValue<string>());
        Assert.Equal(ReplayBackend.Time("2026-10-11T00:00:12Z"), result["_meta"]!["replay"]!["initialTimeMs"]!.GetValue<long>());
        Assert.Equal(8, command.Tools().Count);
        Assert.Contains(command.ResourceUri, command.Tools().ToJsonString());
        Assert.Equal("blob:", Assert.Single(command.Resource()["_meta"]!["ui"]!["csp"]!["resourceDomains"]!.AsArray())!.GetValue<string>());

        var read = await command.InvokeAsync("read_replay_annotation", Parse("""{"sessionId":"capture-1","annotationId":"marker-1"}"""), default);
        Assert.Equal("image", read["content"]![1]!["type"]!.GetValue<string>());
    }

    [Fact]
    public async Task OpenFullReplayLaunchesTheSelectedSessionAtTheCurrentHostAddress()
    {
        using var handler = new RecordingHandler();
        using var http = new HttpClient(handler);
        var opened = new List<Uri>();

        Task<JsonObject> Run(string[] args, CancellationToken token) => args[0] == "host"
            ? Task.FromResult(Parse("""{"isRunning":true,"explorerUrl":"http://127.0.0.1:49000/review/"}"""))
            : handler.Run(args, token);

        var backend = new ReplayBackend(Run, http, url =>
        {
            opened.Add(url);

            return null;
        }
);
        var command = new ReplayMcpCommand("<html></html>", backend);
        var result = await command.InvokeAsync("open_replay_in_browser", Parse("""{"sessionId":"capture-1"}"""), default);

        Assert.Null(result["isError"]);
        Assert.Equal("http://127.0.0.1:49000/review/?sessionId=capture-1", Assert.Single(opened).AbsoluteUri);
        Assert.Equal(opened[0].AbsoluteUri, result["structuredContent"]!["replayUrl"]!.GetValue<string>());

        var tool = command.Tools().Single(item => item!["name"]!.GetValue<string>() == "open_replay_in_browser")!;
        Assert.Equal("app", Assert.Single(tool["_meta"]!["ui"]!["visibility"]!.AsArray())!.GetValue<string>());
        Assert.False(tool["annotations"]!["readOnlyHint"]!.GetValue<bool>());
        Assert.Equal(0, handler.Writes);
    }

    [Fact]
    public async Task BrowserLaunchFailureIsReturnedToTheWidget()
    {
        using var handler = new RecordingHandler();
        using var http = new HttpClient(handler);
        var command = new ReplayMcpCommand("<html></html>", new ReplayBackend(handler.Run, http, _ => "No default browser."));
        var result = await command.InvokeAsync("open_replay_in_browser", Parse("""{"sessionId":"capture-1"}"""), default);
        Assert.True(result["isError"]!.GetValue<bool>());
        Assert.Contains("No default browser.", result["content"]![0]!["text"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("{\"sessionId\":\"missing\"}")]
    [InlineData("{\"sessionId\":\"../capture\"}")]
    [InlineData("{\"sessionId\":\"capture-1\",\"url\":\"https://example.com\"}")]
    public async Task InvalidBrowserRequestsNeverLaunch(string input)
    {
        using var handler = new RecordingHandler();
        using var http = new HttpClient(handler);
        var opened = new List<Uri>();
        var command = new ReplayMcpCommand("<html></html>", new ReplayBackend(handler.Run, http, url =>
        {
            opened.Add(url);

            return null;
        }
));
        var result = await command.InvokeAsync("open_replay_in_browser", Parse(input), default);
        Assert.True(result["isError"]!.GetValue<bool>());
        Assert.Empty(opened);
    }

    [Theory]
    [InlineData("{\"isRunning\":false,\"explorerUrl\":\"http://127.0.0.1:47231/ansight/\"}")]
    [InlineData("{\"isRunning\":true,\"explorerUrl\":\"https://example.com/\"}")]
    public async Task UnavailableOrNonLocalHostsNeverLaunch(string status)
    {
        using var handler = new RecordingHandler();
        using var http = new HttpClient(handler);
        var opened = new List<Uri>();

        Task<JsonObject> Run(string[] args, CancellationToken token) => args[0] == "host"
            ? Task.FromResult(Parse(status)) : handler.Run(args, token);

        var command = new ReplayMcpCommand("<html></html>", new ReplayBackend(Run, http, url =>
        {
            opened.Add(url);

            return null;
        }
));
        var result = await command.InvokeAsync("open_replay_in_browser", Parse("""{"sessionId":"capture-1"}"""), default);
        Assert.True(result["isError"]!.GetValue<bool>());
        Assert.Empty(opened);
    }

    [Fact]
    public async Task NativeAnnotationSaveIsReadBackAndCannotOverwriteAnExistingId()
    {
        using var handler = new RecordingHandler();
        using var http = new HttpClient(handler);
        var command = new ReplayMcpCommand("<html></html>", new ReplayBackend(handler.Run, http));
        var input = Parse("""{"sessionId":"capture-1","annotationId":"new-note","frameId":"frame-a","comment":"Search result\nExpected selected area","geometry":{"kind":1,"x":0.2,"y":0.3,"width":0.2,"height":0.1}}""");
        var saved = await command.InvokeAsync("create_replay_annotation", input, default);
        Assert.Null(saved["isError"]);
        Assert.Equal("new-note", saved["structuredContent"]!["annotationId"]!.GetValue<string>());
        Assert.Null((await command.InvokeAsync("create_replay_annotation", input, default))["isError"]);
        Assert.Equal(1, handler.Writes);
        input["comment"] = "Replacement";
        Assert.True((await command.InvokeAsync("create_replay_annotation", input, default))["isError"]!.GetValue<bool>());
        Assert.Equal(1, handler.Writes);
    }

    [Theory]
    [InlineData("show_replay", "{\"sessionId\":\"../capture\"}")]
    [InlineData("show_replay", "{\"sessionId\":\"capture-1\",\"url\":\"http://example.com\"}")]
    [InlineData("show_replay", "{\"sessionId\":\"capture-1\",\"annotationId\":\"missing\"}")]
    [InlineData("get_replay_frames", "{\"sessionId\":\"capture-1\",\"frameIds\":[\"foreign\"]}")]
    [InlineData("list_replays", "{\"limit\":1000}")]
    [InlineData("create_replay_annotation", "{\"sessionId\":\"capture-1\",\"annotationId\":\"new-note\",\"frameId\":\"frame-a\",\"comment\":\"Test\",\"geometry\":{\"kind\":1,\"x\":0.9,\"y\":0.2,\"width\":0.5,\"height\":0.1}}")]
    public async Task RejectsInvalidInputsWithoutWriting(string tool, string arguments)
    {
        using var handler = new RecordingHandler();
        using var http = new HttpClient(handler);
        var command = new ReplayMcpCommand("<html></html>", new ReplayBackend(handler.Run, http));
        Assert.True((await command.InvokeAsync(tool, Parse(arguments), default))["isError"]!.GetValue<bool>());
        Assert.Equal(0, handler.Writes);
    }

    [Theory]
    [InlineData("https://example.com/")]
    [InlineData("http://127.0.0.1.evil.test/")]
    [InlineData("http://user:pass@localhost/")]
    [InlineData("file:///tmp/")]
    public void ReplayRejectsNonLocalHostUrls(string url) => Assert.Throws<ArgumentException>(() => ReplayBackend.LocalHost(url));

    [Fact]
    public async Task ReplayIncludesVideoTimingAndTransfersVideoOnlyInPrivateBoundedChunks()
    {
        using var handler = new RecordingHandler();
        handler.VideoBytes = new byte[ReplayBackend.VideoChunkBytes + 3];
        handler.Recordings.Add(Parse($$"""
            {"recordingId":"video-1","startedUtc":"2026-10-11T00:00:05Z","durationUs":30000000,"width":840,"height":1280,"byteSize":{{handler.VideoBytes.Length}},"status":"completed"}
            """));
        using var http = new HttpClient(handler);
        var command = new ReplayMcpCommand("<html></html>", new ReplayBackend(handler.Run, http));

        var replay = await command.InvokeAsync("show_replay", Parse("""{"sessionId":"capture-1"}"""), default);

        Assert.Null(replay["isError"]);
        Assert.Equal(1, replay["structuredContent"]!["videoCount"]!.GetValue<int>());
        Assert.Equal(ReplayBackend.Time("2026-10-11T00:00:05Z"), replay["_meta"]!["replay"]!["startMs"]!.GetValue<long>());
        Assert.Equal(ReplayBackend.Time("2026-10-11T00:00:35Z"), replay["_meta"]!["replay"]!["endMs"]!.GetValue<long>());

        var first = await command.InvokeAsync("get_replay_video_chunk", Parse("""{"sessionId":"capture-1","recordingId":"video-1","offset":0}"""), default);
        var last = await command.InvokeAsync("get_replay_video_chunk", Parse($$"""{"sessionId":"capture-1","recordingId":"video-1","offset":{{ReplayBackend.VideoChunkBytes}}}"""), default);

        Assert.Null(first["isError"]);
        Assert.Null(last["isError"]);
        Assert.Equal(ReplayBackend.VideoChunkBytes, Convert.FromBase64String(first["_meta"]!["videoChunk"]!["data"]!.GetValue<string>()).Length);
        Assert.Equal(3, Convert.FromBase64String(last["_meta"]!["videoChunk"]!["data"]!.GetValue<string>()).Length);
        Assert.DoesNotContain("data", first["structuredContent"]!.ToJsonString());
        Assert.Empty(first["content"]!.AsArray());
    }

    [Theory]
    [InlineData("foreign", 0)]
    [InlineData("video-1", -1)]
    [InlineData("video-1", 6)]
    public async Task VideoRejectsForeignRecordingsAndOutOfBoundsOffsetsBeforeReadingBytes(string recordingId, long offset)
    {
        using var handler = new RecordingHandler();
        handler.Recordings.Add(Parse("""{"recordingId":"video-1","startedUtc":"2026-10-11T00:00:05Z","durationUs":30000000,"width":840,"height":1280,"byteSize":6,"status":"completed"}"""));
        using var http = new HttpClient(handler);
        var command = new ReplayMcpCommand("<html></html>", new ReplayBackend(handler.Run, http));

        var result = await command.InvokeAsync("get_replay_video_chunk", ReplayBackend.Json(new { sessionId = "capture-1", recordingId, offset }), default);

        Assert.True(result["isError"]!.GetValue<bool>());
        Assert.Equal(0, handler.VideoReads);
    }

    [Fact]
    public async Task ReplaySupportsVideoWithoutRetainedScreenshots()
    {
        using var handler = new RecordingHandler();
        handler.Recordings.Add(Parse("""{"recordingId":"video-1","startedUtc":"2026-10-11T00:00:05Z","durationUs":30000000,"width":840,"height":1280,"byteSize":6,"status":"completed"}"""));
        using var http = new HttpClient(handler);

        Task<JsonObject> Run(string[] args, CancellationToken token) => args[1] == "images"
            ? Task.FromResult(Parse("""{"items":[]}"""))
            : handler.Run(args, token);

        var command = new ReplayMcpCommand("<html></html>", new ReplayBackend(Run, http));
        var result = await command.InvokeAsync("show_replay", Parse("""{"sessionId":"capture-1"}"""), default);

        Assert.Null(result["isError"]);
        Assert.Equal(0, result["structuredContent"]!["frameCount"]!.GetValue<int>());
        Assert.Equal(1, result["structuredContent"]!["videoCount"]!.GetValue<int>());
        Assert.Empty(result["_meta"]!["frames"]!.AsArray());
    }

    [Fact]
    public async Task VideoRejectsAResponseThatDoesNotHonorTheRequestedRange()
    {
        using var handler = new RecordingHandler { ReturnInvalidRange = true };
        handler.Recordings.Add(Parse("""{"recordingId":"video-1","startedUtc":"2026-10-11T00:00:05Z","durationUs":30000000,"width":840,"height":1280,"byteSize":6,"status":"completed"}"""));
        using var http = new HttpClient(handler);
        var command = new ReplayMcpCommand("<html></html>", new ReplayBackend(handler.Run, http));

        var result = await command.InvokeAsync("get_replay_video_chunk", Parse("""{"sessionId":"capture-1","recordingId":"video-1","offset":0}"""), default);

        Assert.True(result["isError"]!.GetValue<bool>());
        Assert.Contains("unexpected video range", result["content"]![0]!["text"]!.GetValue<string>());
    }
}
