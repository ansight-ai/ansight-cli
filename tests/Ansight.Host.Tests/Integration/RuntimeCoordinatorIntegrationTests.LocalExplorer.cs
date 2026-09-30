using System.Buffers.Binary;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using Ansight.Host.Runtime.BinaryTransfers;
using Ansight.Host.Tests.TestSupport;
using Ansight.Tools;

namespace Ansight.Host.Tests.Integration;

public sealed partial class RuntimeCoordinatorIntegrationTests
{

    [Fact(Timeout = 60000)]
    public async Task LocalExplorerQueriesCatalogBeforeFirstFileRequest()
    {
        using var environment = new TestEnvironment();
        var pairingConfig = environment.SeedPairingConfig(
            "com.example.local-explorer-files",
            "Local Explorer File Test App");
        await using var runtime = environment.CreateRuntime();
        await runtime.StartAsync();

        var connectResponse = await SendConnectRequestAsync(pairingConfig);
        Assert.True(connectResponse.Accepted);
        using var socket = new ClientWebSocket();
        var socketUri = new Uri(
            $"ws://127.0.0.1:{connectResponse.WebSocketPort}{connectResponse.WebSocketPath}?token={connectResponse.WebSocketToken}");
        await socket.ConnectAsync(socketUri, CancellationToken.None);

        string? sessionId = null;
        await TestWait.UntilAsync(
            () =>
            {
                sessionId = runtime.AppTools.GetConnectedSessionIds().SingleOrDefault();
                return sessionId is not null;
            },
            because: "The paired app should appear in the local session explorer.");

        var explorer = await runtime.SessionReplays.StartExplorerAsync(
            new SessionExplorerStartRequest(InitialSessionId: sessionId));
        Assert.True(explorer.IsSuccess, explorer.Message);
        var explorerUrl = Assert.IsType<Uri>(explorer.ExplorerUrl);
        using var httpClient = new HttpClient();
        using var requestContent = new StringContent("{}", Encoding.UTF8, "application/json");
        var listTask = httpClient.PostAsync(
            new Uri(
                explorerUrl,
                $"api/sessions/{Uri.EscapeDataString(sessionId!)}/files/list"),
            requestContent);

        var queryRequest = await ReceiveJsonAsync(socket);
        Assert.Equal(ToolProtocolMessageTypes.QueryType, queryRequest["type"]?.GetValue<string>());
        var queryRequestId = Assert.IsAssignableFrom<JsonValue>(queryRequest["id"]).GetValue<string>();
        await SendJsonAsync(
            socket,
            new JsonObject
            {
                ["type"] = ToolProtocolMessageTypes.CatalogType,
                ["id"] = $"{queryRequestId}.response",
                ["replyTo"] = queryRequestId,
                ["sessionId"] = sessionId,
                ["payload"] = new JsonObject
                {
                    ["tools"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["id"] = "files.list_directory",
                            ["policy"] = "read"
                        }
                    }
                }
            });

        var listRequestTask = ReceiveJsonAsync(socket);
        var completedTask = await Task.WhenAny(listRequestTask, listTask);
        if (ReferenceEquals(completedTask, listTask))
        {
            using var earlyResponse = await listTask;
            Assert.Fail(
                $"The explorer returned before forwarding the file call: {await earlyResponse.Content.ReadAsStringAsync()}");
        }
        var listRequest = await listRequestTask;
        Assert.Equal(ToolProtocolMessageTypes.CallType, listRequest["type"]?.GetValue<string>());
        Assert.Equal("files.list_directory", listRequest["payload"]?["toolId"]?.GetValue<string>());
        var listRequestId = Assert.IsAssignableFrom<JsonValue>(listRequest["id"]).GetValue<string>();
        await SendJsonAsync(
            socket,
            new JsonObject
            {
                ["type"] = ToolProtocolMessageTypes.ResultType,
                ["id"] = $"{listRequestId}.response",
                ["replyTo"] = listRequestId,
                ["sessionId"] = sessionId,
                ["payload"] = new JsonObject
                {
                    ["toolId"] = "files.list_directory",
                    ["success"] = true,
                    ["result"] = new JsonObject
                    {
                        ["rootAlias"] = "default",
                        ["relativePath"] = string.Empty,
                        ["entries"] = new JsonArray()
                    }
                }
            });

        using var listResponse = await listTask;
        var listResponseJson = await listResponse.Content.ReadAsStringAsync();
        Assert.True(
            listResponse.StatusCode == HttpStatusCode.OK,
            $"Expected the file list to succeed, but received {listResponse.StatusCode}: {listResponseJson}");
        var listJson = JsonNode.Parse(listResponseJson)!.AsObject();
        Assert.True(listJson["success"]!.GetValue<bool>());

        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
        await DrainSocketAsync(socket);
    }

    [Fact(Timeout = 60000)]
    public async Task LocalExplorerCapturesLiveFileAsTimelineArtifact()
    {
        using var environment = new TestEnvironment();
        var pairingConfig = environment.SeedPairingConfig(
            "com.example.local-explorer-file-capture",
            "Local Explorer File Capture Test App");
        await using var runtime = environment.CreateRuntime();
        await runtime.StartAsync();

        var connectResponse = await SendConnectRequestAsync(pairingConfig);
        Assert.True(connectResponse.Accepted);
        using var socket = new ClientWebSocket();
        var socketUri = new Uri(
            $"ws://127.0.0.1:{connectResponse.WebSocketPort}{connectResponse.WebSocketPath}?token={connectResponse.WebSocketToken}");
        await socket.ConnectAsync(socketUri, CancellationToken.None);

        string? sessionId = null;
        await TestWait.UntilAsync(
            () =>
            {
                sessionId = runtime.AppTools.GetConnectedSessionIds().SingleOrDefault();
                return sessionId is not null;
            },
            because: "The paired app should appear in the local session explorer.");

        var explorer = await runtime.SessionReplays.StartExplorerAsync(
            new SessionExplorerStartRequest(InitialSessionId: sessionId));
        Assert.True(explorer.IsSuccess, explorer.Message);
        var explorerUrl = Assert.IsType<Uri>(explorer.ExplorerUrl);
        using var httpClient = new HttpClient();
        using var requestContent = new StringContent(
            """{"root":"appData","path":"ansight/offline-sync/data.db3"}""",
            Encoding.UTF8,
            "application/json");
        var captureTask = httpClient.PostAsync(
            new Uri(
                explorerUrl,
                $"api/sessions/{Uri.EscapeDataString(sessionId!)}/files/capture"),
            requestContent);

        var queryRequest = await ReceiveJsonAsync(socket);
        Assert.Equal(ToolProtocolMessageTypes.QueryType, queryRequest["type"]?.GetValue<string>());
        var queryRequestId = Assert.IsAssignableFrom<JsonValue>(queryRequest["id"]).GetValue<string>();
        await SendJsonAsync(
            socket,
            new JsonObject
            {
                ["type"] = ToolProtocolMessageTypes.CatalogType,
                ["id"] = $"{queryRequestId}.response",
                ["replyTo"] = queryRequestId,
                ["sessionId"] = sessionId,
                ["payload"] = new JsonObject
                {
                    ["tools"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["id"] = "files.begin_binary_download",
                            ["policy"] = "read"
                        }
                    }
                }
            });

        var toolRequest = await ReceiveJsonAsync(socket);
        Assert.Equal(ToolProtocolMessageTypes.CallType, toolRequest["type"]?.GetValue<string>());
        Assert.Equal(
            "files.begin_binary_download",
            toolRequest["payload"]?["toolId"]?.GetValue<string>());
        Assert.Equal(
            "appData",
            toolRequest["payload"]?["arguments"]?["root"]?.GetValue<string>());
        Assert.Equal(
            "ansight/offline-sync/data.db3",
            toolRequest["payload"]?["arguments"]?["path"]?.GetValue<string>());
        var toolRequestId = Assert.IsAssignableFrom<JsonValue>(toolRequest["id"]).GetValue<string>();
        var transferId = Guid.NewGuid();
        var fileBytes = new byte[] { 1, 2, 3, 4 };
        await SendJsonAsync(
            socket,
            new JsonObject
            {
                ["type"] = ToolProtocolMessageTypes.ResultType,
                ["id"] = $"{toolRequestId}.response",
                ["replyTo"] = toolRequestId,
                ["sessionId"] = sessionId,
                ["payload"] = new JsonObject
                {
                    ["toolId"] = "files.begin_binary_download",
                    ["success"] = true,
                    ["result"] = new JsonObject
                    {
                        ["rootAlias"] = "appData",
                        ["relativePath"] = "ansight/offline-sync/data.db3",
                        ["fileName"] = "data.db3",
                        ["mimeType"] = "application/vnd.sqlite3",
                        ["sizeBytes"] = fileBytes.Length,
                        ["transferId"] = transferId.ToString("N"),
                        ["deliveryMode"] = "websocket_binary",
                        ["wireProtocol"] = BinaryFileTransferProtocol.ProtocolName,
                        ["status"] = "queued",
                        ["capturedAtUtc"] = "2026-09-06T01:02:03.0000000Z"
                    }
                }
            });
        await socket.SendAsync(
            CreateLiveFileTransferFrame(transferId, BinaryFileTransferFrameType.Chunk, 0, 0, fileBytes),
            WebSocketMessageType.Binary,
            true,
            CancellationToken.None);
        await socket.SendAsync(
            CreateLiveFileTransferFrame(transferId, BinaryFileTransferFrameType.Complete, 1, fileBytes.Length, []),
            WebSocketMessageType.Binary,
            true,
            CancellationToken.None);

        using var captureResponse = await captureTask;
        var captureResponseText = await captureResponse.Content.ReadAsStringAsync();
        Assert.True(
            captureResponse.StatusCode == HttpStatusCode.OK,
            $"Expected the file capture to succeed, but received {captureResponse.StatusCode}: {captureResponseText}");
        var captureJson = JsonNode.Parse(captureResponseText)!.AsObject();
        var artifactSnapshotId = captureJson["artifactSnapshotId"]?.GetValue<string>();
        Assert.False(string.IsNullOrWhiteSpace(artifactSnapshotId));

        AppSessionSnapshot? snapshot = null;
        await TestWait.UntilAsync(
            () => runtime.Sessions.TryGetSnapshot(sessionId!, out snapshot)
                  && snapshot?.ArtifactSnapshots.Count == 1,
            because: "The downloaded live file should be retained as a session artifact.");
        var artifact = Assert.Single(snapshot!.ArtifactSnapshots);
        Assert.Equal(artifactSnapshotId, artifact.SnapshotId);
        Assert.Equal("ansight.app.artifacts", artifact.Source);
        Assert.Equal("files", artifact.RootAlias);
        Assert.Equal("ansight/offline-sync/data.db3", artifact.RelativePath);
        Assert.Equal(fileBytes.Length, artifact.ByteCount);
        Assert.Equal("data.db3", Assert.Single(artifact.Entries).Name);

        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
        await DrainSocketAsync(socket);
    }

    private static byte[] CreateLiveFileTransferFrame(
        Guid transferId,
        BinaryFileTransferFrameType frameType,
        int sequence,
        long offsetBytes,
        byte[] payload)
    {
        var frame = new byte[BinaryFileTransferProtocol.HeaderSize + payload.Length];
        frame[0] = (byte)'A';
        frame[1] = (byte)'S';
        frame[2] = (byte)'F';
        frame[3] = (byte)'T';
        frame[4] = 1;
        frame[5] = (byte)frameType;
        Encoding.ASCII.GetBytes(transferId.ToString("N")).CopyTo(frame, 8);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(40, 4), sequence);
        BinaryPrimitives.WriteInt64LittleEndian(frame.AsSpan(44, 8), offsetBytes);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(52, 4), payload.Length);
        payload.CopyTo(frame, BinaryFileTransferProtocol.HeaderSize);
        return frame;
    }

    [Fact(Timeout = 60000)]
    public async Task LocalExplorerCapturesAndPersistsLiveVisualTree()
    {
        using var environment = new TestEnvironment();
        var pairingConfig = environment.SeedPairingConfig(
            "com.example.local-explorer",
            "Local Explorer Test App");
        await using var runtime = environment.CreateRuntime();
        await runtime.StartAsync();

        var connectResponse = await SendConnectRequestAsync(pairingConfig);
        Assert.True(connectResponse.Accepted);
        using var socket = new ClientWebSocket();
        var socketUri = new Uri(
            $"ws://127.0.0.1:{connectResponse.WebSocketPort}{connectResponse.WebSocketPath}?token={connectResponse.WebSocketToken}");
        await socket.ConnectAsync(socketUri, CancellationToken.None);

        string? sessionId = null;
        await TestWait.UntilAsync(
            () =>
            {
                sessionId = runtime.AppTools.GetConnectedSessionIds().SingleOrDefault();
                return sessionId is not null;
            },
            because: "The paired app should appear in the local session explorer.");

        var explorer = await runtime.SessionReplays.StartExplorerAsync(
            new SessionExplorerStartRequest(InitialSessionId: sessionId));
        Assert.True(explorer.IsSuccess, explorer.Message);
        var explorerUrl = Assert.IsType<Uri>(explorer.ExplorerUrl);
        using var httpClient = new HttpClient();
        using var requestContent = new StringContent("{}", Encoding.UTF8, "application/json");
        var captureTask = httpClient.PostAsync(
            new Uri(
                explorerUrl,
                $"api/sessions/{Uri.EscapeDataString(sessionId!)}/visual-tree/capture"),
            requestContent);

        var queryRequest = await ReceiveJsonAsync(socket);
        Assert.Equal(ToolProtocolMessageTypes.QueryType, queryRequest["type"]?.GetValue<string>());
        var queryRequestId = Assert.IsAssignableFrom<JsonValue>(queryRequest["id"]).GetValue<string>();
        await SendJsonAsync(
            socket,
            new JsonObject
            {
                ["type"] = ToolProtocolMessageTypes.CatalogType,
                ["id"] = $"{queryRequestId}.response",
                ["replyTo"] = queryRequestId,
                ["sessionId"] = sessionId,
                ["payload"] = new JsonObject
                {
                    ["tools"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["id"] = "maui.get_visual_tree",
                            ["policy"] = "read"
                        },
                        new JsonObject
                        {
                            ["id"] = "files.list_directory",
                            ["policy"] = "read"
                        },
                        new JsonObject
                        {
                            ["id"] = "files.read_file",
                            ["policy"] = "read"
                        },
                        new JsonObject
                        {
                            ["id"] = "files.download_file",
                            ["policy"] = "read"
                        }
                    }
                }
            });

        var toolRequest = await ReceiveJsonAsync(socket);
        Assert.Equal(ToolProtocolMessageTypes.CallType, toolRequest["type"]?.GetValue<string>());
        var toolRequestId = Assert.IsAssignableFrom<JsonValue>(toolRequest["id"]).GetValue<string>();
        Assert.Equal(
            "maui.get_visual_tree",
            toolRequest["payload"]?["toolId"]?.GetValue<string>());
        await SendJsonAsync(
            socket,
            new JsonObject
            {
                ["type"] = ToolProtocolMessageTypes.ResultType,
                ["id"] = $"{toolRequestId}.response",
                ["replyTo"] = toolRequestId,
                ["sessionId"] = sessionId,
                ["payload"] = new JsonObject
                {
                    ["toolId"] = "maui.get_visual_tree",
                    ["success"] = true,
                    ["result"] = new JsonObject
                    {
                        ["format"] = "ansight.visual-tree.compact.v2",
                        ["platform"] = "maui",
                        ["capturedAtUtc"] = DateTimeOffset.UtcNow,
                        ["nodeCount"] = 1,
                        ["types"] = new JsonArray("ContentPage"),
                        ["root"] = new JsonObject
                        {
                            ["id"] = "root",
                            ["typeId"] = 0,
                            ["bounds"] = new JsonArray(0, 0, 1024, 768),
                            ["children"] = new JsonArray()
                        }
                    }
                }
            });

        using var captureResponse = await captureTask;
        Assert.Equal(HttpStatusCode.OK, captureResponse.StatusCode);
        var captureJson = JsonNode.Parse(await captureResponse.Content.ReadAsStringAsync())!.AsObject();
        Assert.True(captureJson["isSuccess"]!.GetValue<bool>());
        Assert.Equal(1, captureJson["nodeCount"]!.GetValue<int>());
        Assert.False(string.IsNullOrWhiteSpace(captureJson["snapshotId"]!.GetValue<string>()));

        Assert.True(runtime.Sessions.TryGetSnapshot(sessionId!, out var snapshot));
        var visualTree = Assert.Single(snapshot!.VisualTreeSnapshots);
        Assert.Equal("maui.get_visual_tree", visualTree.Source);
        Assert.Equal(1, visualTree.NodeCount);

        using var listContent = new StringContent(
            "{\"root\":\"appData\",\"path\":\"Documents\",\"includeHidden\":false}",
            Encoding.UTF8,
            "application/json");
        var listTask = httpClient.PostAsync(
            new Uri(
                explorerUrl,
                $"api/sessions/{Uri.EscapeDataString(sessionId!)}/files/list"),
            listContent);
        var listRequest = await ReceiveJsonAsync(socket);
        Assert.Equal("files.list_directory", listRequest["payload"]?["toolId"]?.GetValue<string>());
        var listRequestId = Assert.IsAssignableFrom<JsonValue>(listRequest["id"]).GetValue<string>();
        Assert.Equal("appData", listRequest["payload"]?["arguments"]?["root"]?.GetValue<string>());
        Assert.Equal("Documents", listRequest["payload"]?["arguments"]?["path"]?.GetValue<string>());
        await SendJsonAsync(
            socket,
            new JsonObject
            {
                ["type"] = ToolProtocolMessageTypes.ResultType,
                ["id"] = $"{listRequestId}.response",
                ["replyTo"] = listRequestId,
                ["sessionId"] = sessionId,
                ["payload"] = new JsonObject
                {
                    ["toolId"] = "files.list_directory",
                    ["success"] = true,
                    ["result"] = new JsonObject
                    {
                        ["rootAlias"] = "appData",
                        ["relativePath"] = "Documents",
                        ["entries"] = new JsonArray
                        {
                            new JsonObject
                            {
                                ["name"] = "settings.json",
                                ["relativePath"] = "Documents/settings.json",
                                ["rootAlias"] = "appData",
                                ["kind"] = "file",
                                ["sizeBytes"] = 12,
                                ["mimeType"] = "application/json"
                            }
                        }
                    }
                }
            });
        using var listResponse = await listTask;
        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        var listJson = JsonNode.Parse(await listResponse.Content.ReadAsStringAsync())!.AsObject();
        Assert.True(listJson["success"]!.GetValue<bool>());
        Assert.Equal(
            "settings.json",
            listJson["envelope"]?["payload"]?["result"]?["entries"]?[0]?["name"]?.GetValue<string>());

        using var searchContent = new StringContent(
            "{\"root\":\"appData\",\"path\":\"Documents\",\"recursive\":true,\"maxDepth\":16,\"maxEntries\":1000}",
            Encoding.UTF8,
            "application/json");
        var searchTask = httpClient.PostAsync(
            new Uri(
                explorerUrl,
                $"api/sessions/{Uri.EscapeDataString(sessionId!)}/files/list"),
            searchContent);
        var searchRequest = await ReceiveJsonAsync(socket);
        Assert.Equal("files.list_directory", searchRequest["payload"]?["toolId"]?.GetValue<string>());
        var searchRequestId = Assert.IsAssignableFrom<JsonValue>(searchRequest["id"]).GetValue<string>();
        Assert.True(searchRequest["payload"]?["arguments"]?["recursive"]?.GetValue<bool>());
        Assert.Equal(16, searchRequest["payload"]?["arguments"]?["maxDepth"]?.GetValue<int>());
        Assert.Equal(1000, searchRequest["payload"]?["arguments"]?["maxEntries"]?.GetValue<int>());
        await SendJsonAsync(
            socket,
            new JsonObject
            {
                ["type"] = ToolProtocolMessageTypes.ResultType,
                ["id"] = $"{searchRequestId}.response",
                ["replyTo"] = searchRequestId,
                ["sessionId"] = sessionId,
                ["payload"] = new JsonObject
                {
                    ["toolId"] = "files.list_directory",
                    ["success"] = true,
                    ["result"] = new JsonObject
                    {
                        ["rootAlias"] = "appData",
                        ["relativePath"] = "Documents",
                        ["entries"] = new JsonArray
                        {
                            new JsonObject
                            {
                                ["name"] = "asset.glb",
                                ["relativePath"] = "Documents/Models/asset.glb",
                                ["rootAlias"] = "appData",
                                ["kind"] = "file",
                                ["sizeBytes"] = 5,
                                ["mimeType"] = "model/gltf-binary"
                            }
                        }
                    }
                }
            });
        using var searchResponse = await searchTask;
        Assert.Equal(HttpStatusCode.OK, searchResponse.StatusCode);
        var searchJson = JsonNode.Parse(await searchResponse.Content.ReadAsStringAsync())!.AsObject();
        Assert.Equal(
            "Documents/Models/asset.glb",
            searchJson["envelope"]?["payload"]?["result"]?["entries"]?[0]?["relativePath"]?.GetValue<string>());

        using var readContent = new StringContent(
            "{\"root\":\"appData\",\"path\":\"Documents/settings.json\"}",
            Encoding.UTF8,
            "application/json");
        var readTask = httpClient.PostAsync(
            new Uri(
                explorerUrl,
                $"api/sessions/{Uri.EscapeDataString(sessionId!)}/files/read"),
            readContent);
        var readRequest = await ReceiveJsonAsync(socket);
        Assert.Equal("files.read_file", readRequest["payload"]?["toolId"]?.GetValue<string>());
        var readRequestId = Assert.IsAssignableFrom<JsonValue>(readRequest["id"]).GetValue<string>();
        Assert.Equal(512 * 1024, readRequest["payload"]?["arguments"]?["maxBytes"]?.GetValue<int>());
        await SendJsonAsync(
            socket,
            new JsonObject
            {
                ["type"] = ToolProtocolMessageTypes.ResultType,
                ["id"] = $"{readRequestId}.response",
                ["replyTo"] = readRequestId,
                ["sessionId"] = sessionId,
                ["payload"] = new JsonObject
                {
                    ["toolId"] = "files.read_file",
                    ["success"] = true,
                    ["result"] = new JsonObject
                    {
                        ["fileName"] = "settings.json",
                        ["contentType"] = "application/json",
                        ["sizeBytes"] = 12,
                        ["bytesRead"] = 12,
                        ["truncated"] = false,
                        ["encoding"] = "utf8",
                        ["text"] = "{\"ok\":true}"
                    }
                }
            });
        using var readResponse = await readTask;
        Assert.Equal(HttpStatusCode.OK, readResponse.StatusCode);
        var readJson = JsonNode.Parse(await readResponse.Content.ReadAsStringAsync())!.AsObject();
        Assert.Equal(
            "{\"ok\":true}",
            readJson["envelope"]?["payload"]?["result"]?["text"]?.GetValue<string>());

        using var previewContent = new StringContent(
            "{\"root\":\"appData\",\"path\":\"Documents/settings.json\"}",
            Encoding.UTF8,
            "application/json");
        var previewTask = httpClient.PostAsync(
            new Uri(
                explorerUrl,
                $"api/sessions/{Uri.EscapeDataString(sessionId!)}/files/preview"),
            previewContent);
        var previewRequest = await ReceiveJsonAsync(socket);
        Assert.Equal("files.download_file", previewRequest["payload"]?["toolId"]?.GetValue<string>());
        var previewRequestId = Assert.IsAssignableFrom<JsonValue>(previewRequest["id"]).GetValue<string>();
        Assert.Equal(1024 * 1024, previewRequest["payload"]?["arguments"]?["maxBytes"]?.GetValue<int>());
        Assert.Equal("base64", previewRequest["payload"]?["arguments"]?["encoding"]?.GetValue<string>());
        await SendJsonAsync(
            socket,
            new JsonObject
            {
                ["type"] = ToolProtocolMessageTypes.ResultType,
                ["id"] = $"{previewRequestId}.response",
                ["replyTo"] = previewRequestId,
                ["sessionId"] = sessionId,
                ["payload"] = new JsonObject
                {
                    ["toolId"] = "files.download_file",
                    ["success"] = true,
                    ["result"] = new JsonObject
                    {
                        ["fileName"] = "settings.json",
                        ["mimeType"] = "application/json",
                        ["sizeBytes"] = 11,
                        ["version"] = "11:123",
                        ["bytesRead"] = 11,
                        ["hasMore"] = false,
                        ["encoding"] = "base64",
                        ["base64"] = Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"ok\":true}"))
                    }
                }
            });
        using var previewResponse = await previewTask;
        Assert.Equal(HttpStatusCode.OK, previewResponse.StatusCode);
        var previewJson = JsonNode.Parse(await previewResponse.Content.ReadAsStringAsync())!.AsObject();
        Assert.True(previewJson["isSuccess"]!.GetValue<bool>());
        Assert.Equal(FileViewerKinds.Json, previewJson["viewerKind"]!.GetValue<string>());
        Assert.Contains('\n', previewJson["text"]!.GetValue<string>());
        Assert.Equal("{\"ok\":true}", previewJson["rawText"]!.GetValue<string>());

        using var forcedTextContent = new StringContent(
            "{\"root\":\"appData\",\"path\":\"Documents/opaque.data\",\"forceText\":true}",
            Encoding.UTF8,
            "application/json");
        var forcedTextTask = httpClient.PostAsync(
            new Uri(
                explorerUrl,
                $"api/sessions/{Uri.EscapeDataString(sessionId!)}/files/preview"),
            forcedTextContent);
        var forcedTextRequest = await ReceiveJsonAsync(socket);
        Assert.Equal("files.download_file", forcedTextRequest["payload"]?["toolId"]?.GetValue<string>());
        var forcedTextRequestId = Assert.IsAssignableFrom<JsonValue>(forcedTextRequest["id"]).GetValue<string>();
        Assert.Equal(1024 * 1024, forcedTextRequest["payload"]?["arguments"]?["maxBytes"]?.GetValue<int>());
        Assert.Equal(
            "base64",
            forcedTextRequest["payload"]?["arguments"]?["encoding"]?.GetValue<string>());
        await SendJsonAsync(
            socket,
            new JsonObject
            {
                ["type"] = ToolProtocolMessageTypes.ResultType,
                ["id"] = $"{forcedTextRequestId}.response",
                ["replyTo"] = forcedTextRequestId,
                ["sessionId"] = sessionId,
                ["payload"] = new JsonObject
                {
                    ["toolId"] = "files.download_file",
                    ["success"] = true,
                    ["result"] = new JsonObject
                    {
                        ["fileName"] = "opaque.data",
                        ["mimeType"] = "application/octet-stream",
                        ["sizeBytes"] = 11,
                        ["version"] = "11:123",
                        ["bytesRead"] = 11,
                        ["hasMore"] = false,
                        ["encoding"] = "base64",
                        ["base64"] = Convert.ToBase64String(Encoding.UTF8.GetBytes("forced text"))
                    }
                }
            });
        using var forcedTextResponse = await forcedTextTask;
        Assert.Equal(HttpStatusCode.OK, forcedTextResponse.StatusCode);
        var forcedTextJson = JsonNode.Parse(
            await forcedTextResponse.Content.ReadAsStringAsync())!.AsObject();
        Assert.Equal(FileViewerKinds.Text, forcedTextJson["viewerKind"]!.GetValue<string>());
        Assert.Equal("forced text", forcedTextJson["text"]!.GetValue<string>());
        Assert.Equal("forced text", forcedTextJson["rawText"]!.GetValue<string>());
        Assert.Null(forcedTextJson["base64"]);

        using var modelPreviewContent = new StringContent(
            "{\"root\":\"appData\",\"path\":\"Models/asset.glb\"}",
            Encoding.UTF8,
            "application/json");
        using var modelPreviewResponse = await httpClient.PostAsync(
            new Uri(
                explorerUrl,
                $"api/sessions/{Uri.EscapeDataString(sessionId!)}/files/preview"),
            modelPreviewContent);
        Assert.Equal(HttpStatusCode.OK, modelPreviewResponse.StatusCode);
        var modelPreviewJson = JsonNode.Parse(
            await modelPreviewResponse.Content.ReadAsStringAsync())!.AsObject();
        Assert.True(modelPreviewJson["isSuccess"]!.GetValue<bool>());
        Assert.Equal(FileViewerKinds.Model3D, modelPreviewJson["viewerKind"]!.GetValue<string>());
        Assert.False(modelPreviewJson["isTruncated"]!.GetValue<bool>());
        Assert.Null(modelPreviewJson["base64"]);

        var modelContentTask = httpClient.GetAsync(
            new Uri(
                explorerUrl,
                $"api/sessions/{Uri.EscapeDataString(sessionId!)}/files/content"
                + "?root=appData&path=Models%2Fasset.glb"));
        var firstModelChunkRequest = await ReceiveJsonAsync(socket);
        Assert.Equal(
            "files.download_file",
            firstModelChunkRequest["payload"]?["toolId"]?.GetValue<string>());
        var firstModelChunkRequestId = Assert.IsAssignableFrom<JsonValue>(
            firstModelChunkRequest["id"]).GetValue<string>();
        Assert.Equal(
            0L,
            firstModelChunkRequest["payload"]?["arguments"]?["offsetBytes"]?.GetValue<long>());
        Assert.Equal(
            1,
            firstModelChunkRequest["payload"]?["arguments"]?["maxBytes"]?.GetValue<int>());
        Assert.Equal(
            "base64",
            firstModelChunkRequest["payload"]?["arguments"]?["encoding"]?.GetValue<string>());
        Assert.Null(firstModelChunkRequest["payload"]?["arguments"]?["expectedVersion"]);
        await SendJsonAsync(
            socket,
            new JsonObject
            {
                ["type"] = ToolProtocolMessageTypes.ResultType,
                ["id"] = $"{firstModelChunkRequestId}.response",
                ["replyTo"] = firstModelChunkRequestId,
                ["sessionId"] = sessionId,
                ["payload"] = new JsonObject
                {
                    ["toolId"] = "files.download_file",
                    ["success"] = true,
                    ["result"] = new JsonObject
                    {
                        ["fileName"] = "asset.glb",
                        ["mimeType"] = "model/gltf-binary",
                        ["sizeBytes"] = 5,
                        ["version"] = "5:123",
                        ["base64"] = Convert.ToBase64String(new byte[] { 1 }),
                        ["hasMore"] = true,
                        ["nextOffsetBytes"] = 1
                    }
                }
            });

        var secondModelChunkRequest = await ReceiveJsonAsync(socket);
        Assert.Equal(
            "files.download_file",
            secondModelChunkRequest["payload"]?["toolId"]?.GetValue<string>());
        var secondModelChunkRequestId = Assert.IsAssignableFrom<JsonValue>(
            secondModelChunkRequest["id"]).GetValue<string>();
        Assert.Equal(
            1L,
            secondModelChunkRequest["payload"]?["arguments"]?["offsetBytes"]?.GetValue<long>());
        Assert.Equal(
            4,
            secondModelChunkRequest["payload"]?["arguments"]?["maxBytes"]?.GetValue<int>());
        Assert.Equal(
            "5:123",
            secondModelChunkRequest["payload"]?["arguments"]?["expectedVersion"]?.GetValue<string>());
        await SendJsonAsync(
            socket,
            new JsonObject
            {
                ["type"] = ToolProtocolMessageTypes.ResultType,
                ["id"] = $"{secondModelChunkRequestId}.response",
                ["replyTo"] = secondModelChunkRequestId,
                ["sessionId"] = sessionId,
                ["payload"] = new JsonObject
                {
                    ["toolId"] = "files.download_file",
                    ["success"] = true,
                    ["result"] = new JsonObject
                    {
                        ["fileName"] = "asset.glb",
                        ["mimeType"] = "model/gltf-binary",
                        ["sizeBytes"] = 5,
                        ["version"] = "5:123",
                        ["base64"] = Convert.ToBase64String(new byte[] { 2, 3, 4, 5 }),
                        ["hasMore"] = false,
                        ["nextOffsetBytes"] = 5
                    }
                }
            });

        using var modelContentResponse = await modelContentTask;
        Assert.Equal(HttpStatusCode.OK, modelContentResponse.StatusCode);
        Assert.Equal("model/gltf-binary", modelContentResponse.Content.Headers.ContentType?.MediaType);
        Assert.Equal(5L, modelContentResponse.Content.Headers.ContentLength);
        Assert.Equal(
            new byte[] { 1, 2, 3, 4, 5 },
            await modelContentResponse.Content.ReadAsByteArrayAsync());

        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
        await DrainSocketAsync(socket);
    }
}
