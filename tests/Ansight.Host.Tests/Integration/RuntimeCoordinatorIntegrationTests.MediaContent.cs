using Ansight.Host.Tests.TestSupport;
using Ansight.Tools;

namespace Ansight.Host.Tests.Integration;

public sealed partial class RuntimeCoordinatorIntegrationTests
{
    [Theory(Timeout = 60000)]
    [InlineData("bytes=2-4", 2, 3, "bytes 2-4/10")]
    [InlineData("bytes=-2", 8, 2, "bytes 8-9/10")]
    [InlineData("bytes=0-0", 0, 1, "bytes 0-0/10")]
    public async Task LiveMediaContentForwardsRequestedOffsetsAndPreservesFileVersion(string rangeHeader, int offset, int count, string contentRange)
    {
        using var environment = new TestEnvironment();
        var pairingConfig = environment.SeedPairingConfig("com.example.live-media", "Live Media");
        await using var runtime = environment.CreateRuntime();
        await runtime.StartAsync();
        var connectResponse = await SendConnectRequestAsync(pairingConfig);
        Assert.True(connectResponse.Accepted);
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri(
            $"ws://127.0.0.1:{connectResponse.WebSocketPort}{connectResponse.WebSocketPath}?token={connectResponse.WebSocketToken}"), CancellationToken.None);
        string? sessionId = null;
        await TestWait.UntilAsync(() =>
        {
            sessionId = runtime.AppTools.GetConnectedSessionIds().SingleOrDefault();
            return sessionId is not null;
        });

        var explorer = await runtime.SessionReplays.StartExplorerAsync(new SessionExplorerStartRequest(InitialSessionId: sessionId));
        Assert.True(explorer.IsSuccess, explorer.Message);
        using var client = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(Assert.IsType<Uri>(explorer.ExplorerUrl),
            $"api/sessions/{sessionId}/files/content?root=appData&path=recording.wav"));
        request.Headers.TryAddWithoutValidation("Range", rangeHeader);
        var responseTask = client.SendAsync(request);

        var catalogRequest = await ReceiveJsonAsync(socket);
        Assert.Equal(ToolProtocolMessageTypes.QueryType, catalogRequest["type"]?.GetValue<string>());
        var catalogRequestId = catalogRequest["id"]!.GetValue<string>();
        await SendJsonAsync(socket, new JsonObject
        {
            ["type"] = ToolProtocolMessageTypes.CatalogType,
            ["id"] = $"{catalogRequestId}.response",
            ["replyTo"] = catalogRequestId,
            ["sessionId"] = sessionId,
            ["payload"] = new JsonObject
            {
                ["tools"] = new JsonArray(new JsonObject { ["id"] = "files.download_file", ["policy"] = "read" })
            }
        });

        var metadataRequest = await ReceiveJsonAsync(socket);
        var metadataArguments = metadataRequest["payload"]!["arguments"]!.AsObject();
        Assert.Equal("files.download_file", metadataRequest["payload"]!["toolId"]!.GetValue<string>());
        Assert.Equal(0, metadataArguments["offsetBytes"]!.GetValue<long>());
        Assert.Equal(1, metadataArguments["maxBytes"]!.GetValue<int>());
        Assert.Equal("appData", metadataArguments["root"]!.GetValue<string>());
        await ReplyWithMediaChunkAsync(socket, sessionId!, metadataRequest, 0, [0]);

        byte[] expected = Enumerable.Range(offset, count).Select(value => (byte)value).ToArray();
        if (offset > 0)
        {
            var chunkRequest = await ReceiveJsonAsync(socket);
            var chunkArguments = chunkRequest["payload"]!["arguments"]!.AsObject();
            Assert.Equal(offset, chunkArguments["offsetBytes"]!.GetValue<long>());
            Assert.Equal(count, chunkArguments["maxBytes"]!.GetValue<int>());
            Assert.Equal("media-version-1", chunkArguments["expectedVersion"]!.GetValue<string>());
            await ReplyWithMediaChunkAsync(socket, sessionId!, chunkRequest, offset, expected);
        }

        using var response = await responseTask;
        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal("audio/wav", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(contentRange, response.Content.Headers.ContentRange?.ToString());
        Assert.Equal(count, response.Content.Headers.ContentLength);
        Assert.Contains("bytes", response.Headers.AcceptRanges);
        Assert.Equal(expected, await response.Content.ReadAsByteArrayAsync());

        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
        await DrainSocketAsync(socket);
    }

    private static Task ReplyWithMediaChunkAsync(ClientWebSocket socket, string sessionId, JsonObject request, int offset, byte[] bytes)
    {
        var requestId = request["id"]!.GetValue<string>();
        return SendJsonAsync(socket, new JsonObject
        {
            ["type"] = ToolProtocolMessageTypes.ResultType,
            ["id"] = $"{requestId}.response",
            ["replyTo"] = requestId,
            ["sessionId"] = sessionId,
            ["payload"] = new JsonObject
            {
                ["toolId"] = "files.download_file",
                ["success"] = true,
                ["result"] = new JsonObject
                {
                    ["fileName"] = "recording.wav",
                    ["mimeType"] = "application/octet-stream",
                    ["sizeBytes"] = 10,
                    ["base64"] = Convert.ToBase64String(bytes),
                    ["version"] = "media-version-1",
                    ["hasMore"] = offset + bytes.Length < 10,
                    ["nextOffsetBytes"] = offset + bytes.Length
                }
            }
        });
    }
}
