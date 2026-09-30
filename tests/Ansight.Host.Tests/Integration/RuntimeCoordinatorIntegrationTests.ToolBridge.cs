using System.Buffers.Binary;
using System.IO.Compression;
using Ansight.Host.Tests.TestSupport;
using Ansight.Tools;
using AppLifecycleState = global::Ansight.AppLifecycleState;
using SharpZipEntry = ICSharpCode.SharpZipLib.Zip.ZipEntry;
using SharpZipOutputStream = ICSharpCode.SharpZipLib.Zip.ZipOutputStream;

namespace Ansight.Host.Tests.Integration;

public sealed partial class RuntimeCoordinatorIntegrationTests
{
    [Fact(Timeout = 60000)]
    public async Task SessionHandshake_CapturesToolAndArtifactCatalogForHistoricalAuthoring()
    {
        using var environment = new TestEnvironment();
        var pairingConfig = environment.SeedPairingConfig("com.example.authoring-catalog", "Authoring Catalog App");
        using var runtime = environment.CreateRuntime();

        await runtime.StartAsync();

        var connectResponse = await SendConnectRequestAsync(pairingConfig);
        Assert.True(connectResponse.Accepted);
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(
            new Uri($"ws://127.0.0.1:{connectResponse.WebSocketPort}{connectResponse.WebSocketPath}?token={connectResponse.WebSocketToken}"),
            CancellationToken.None);

        string? sessionId = null;
        await TestWait.UntilAsync(
            () =>
            {
                sessionId = runtime.AppTools.GetConnectedSessionIds().SingleOrDefault();
                return sessionId is not null;
            },
            because: "The paired app should appear as a connected tool session.");

        var catalogRequest = await ReceiveJsonAsync(socket);
        Assert.Equal(ToolProtocolMessageTypes.QueryType, catalogRequest["type"]?.GetValue<string>());
        var catalogRequestId = Assert.IsAssignableFrom<JsonValue>(catalogRequest["id"]).GetValue<string>();
        await SendJsonAsync(socket, new JsonObject
        {
            ["type"] = ToolProtocolMessageTypes.CatalogType,
            ["id"] = $"{catalogRequestId}.response",
            ["replyTo"] = catalogRequestId,
            ["sessionId"] = sessionId,
            ["payload"] = new JsonObject
            {
                ["tools"] = new JsonArray(
                    new JsonObject
                    {
                        ["id"] = "artifacts.query",
                        ["policy"] = "read",
                        ["argumentsSchema"] = new JsonObject { ["type"] = "object" }
                    },
                    new JsonObject
                    {
                        ["id"] = "map.query_state",
                        ["title"] = "Query map state",
                        ["policy"] = "read",
                        ["argumentsSchema"] = new JsonObject { ["type"] = "object" },
                        ["resultSchema"] = new JsonObject { ["type"] = "object" }
                    })
            }
        });

        var artifactRequest = await ReceiveJsonAsync(socket);
        Assert.Equal(ToolProtocolMessageTypes.CallType, artifactRequest["type"]?.GetValue<string>());
        Assert.Equal("artifacts.query", artifactRequest["payload"]?["toolId"]?.GetValue<string>());
        var artifactRequestId = Assert.IsAssignableFrom<JsonValue>(artifactRequest["id"]).GetValue<string>();
        await SendJsonAsync(socket, new JsonObject
        {
            ["type"] = ToolProtocolMessageTypes.ResultType,
            ["id"] = $"{artifactRequestId}.response",
            ["replyTo"] = artifactRequestId,
            ["sessionId"] = sessionId,
            ["payload"] = new JsonObject
            {
                ["toolId"] = "artifacts.query",
                ["success"] = true,
                ["result"] = new JsonObject
                {
                    ["providers"] = new JsonArray(new JsonObject
                    {
                        ["id"] = "map.exports",
                        ["name"] = "Map exports",
                        ["artifacts"] = new JsonArray(new JsonObject
                        {
                            ["id"] = "offline-area",
                            ["name"] = "Offline area"
                        })
                    })
                }
            }
        });

        AppSessionSnapshot? snapshot = null;
        await TestWait.UntilAsync(
            () => runtime.Sessions.TryGetSnapshot(sessionId!, out snapshot)
                  && snapshot?.AppToolCatalog?.ArtifactCatalog is not null,
            because: "The handshake catalog and artifact providers should be retained on the session.");
        Assert.Contains(
            snapshot!.AppToolCatalog!.ToolCatalog["tools"]!.AsArray(),
            tool => tool?["id"]?.GetValue<string>() == "map.query_state");
        Assert.Equal(
            "map.exports",
            snapshot.AppToolCatalog.ArtifactCatalog?["result"]?["providers"]?[0]?["id"]?.GetValue<string>());

        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
        await DrainSocketAsync(socket);
    }

    [Fact(Timeout = 60000)]
    public async Task CallAppToolAsync_ListOpenFileDescriptors_PersistsDiagnosticTimelineArtifact()
    {
        using var environment = new TestEnvironment();
        var pairingConfig = environment.SeedPairingConfig("com.example.file-descriptors", "File Descriptor Test App");
        using var runtime = environment.CreateRuntime();

        await runtime.StartAsync();

        var connectResponse = await SendConnectRequestAsync(pairingConfig);
        Assert.True(connectResponse.Accepted);
        Assert.NotNull(connectResponse.WebSocketPort);
        Assert.False(string.IsNullOrWhiteSpace(connectResponse.WebSocketToken));

        using var socket = new ClientWebSocket();
        var socketUri = new Uri(
            $"ws://127.0.0.1:{connectResponse.WebSocketPort}{connectResponse.WebSocketPath}?token={connectResponse.WebSocketToken}");
        await socket.ConnectAsync(socketUri, CancellationToken.None);

        string? sessionId = null;
        await TestWait.UntilAsync(
            () =>
            {
                var connectedSessions = runtime.AppTools.GetConnectedSessionIds();
                if (connectedSessions.Count != 1)
                {
                    return false;
                }

                sessionId = connectedSessions[0];
                return true;
            },
            because: "The paired app should appear as a connected tool session.");

        var unqueriedCall = await runtime.AppTools.CallAsync(sessionId!, "demo.missing", new JsonObject());
        Assert.False(unqueriedCall.Success);
        Assert.Contains("Query the authenticated tool catalog", unqueriedCall.Message, StringComparison.Ordinal);
        Assert.Contains("demo.missing", unqueriedCall.Message, StringComparison.Ordinal);

        var queryTask = runtime.AppTools.QueryAsync(sessionId!);
        var queryRequest = await ReceiveJsonAsync(socket);
        var queryRequestId = queryRequest["id"]?.GetValue<string>();
        Assert.False(string.IsNullOrWhiteSpace(queryRequestId));
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
                            ["id"] = "file_descriptors.list_open",
                            ["policy"] = "read"
                        }
                    }
                }
            });
        var queryResponse = await queryTask;
        Assert.True(queryResponse.Success);

        var toolCallTask = runtime.AppTools.CallAsync(
            sessionId!,
            "file_descriptors.list_open",
            new JsonObject { ["maxEntries"] = 256 });
        var toolRequest = await ReceiveJsonAsync(socket);
        var requestId = toolRequest["id"]?.GetValue<string>();
        Assert.False(string.IsNullOrWhiteSpace(requestId));
        await SendJsonAsync(
            socket,
            new JsonObject
            {
                ["type"] = ToolProtocolMessageTypes.ResultType,
                ["id"] = $"{requestId}.response",
                ["replyTo"] = requestId,
                ["sessionId"] = sessionId,
                ["payload"] = new JsonObject
                {
                    ["toolId"] = "file_descriptors.list_open",
                    ["success"] = true,
                    ["result"] = new JsonObject
                    {
                        ["capturedAtUtc"] = "2026-08-20T01:02:03.0000000Z",
                        ["count"] = 1,
                        ["matchedCount"] = 1,
                        ["returnedCount"] = 1,
                        ["descriptors"] = new JsonArray
                        {
                            new JsonObject
                            {
                                ["descriptor"] = 4,
                                ["kind"] = "file",
                                ["target"] = "/data/user/0/com.example.file-descriptors/cache/example.db"
                            }
                        },
                        ["truncated"] = false
                    }
                }
            });

        var response = await toolCallTask;
        Assert.True(response.Success, response.Message);

        AppSessionSnapshot? snapshot = null;
        await TestWait.UntilAsync(
            () => runtime.Sessions.TryGetSnapshot(sessionId!, out snapshot)
                  && snapshot?.ArtifactSnapshots.Count == 1,
            because: "The descriptor result should be promoted to a durable session artifact.");
        var artifact = Assert.Single(snapshot!.ArtifactSnapshots);
        Assert.Equal("file-descriptor-snapshot", artifact.Kind);
        Assert.Equal("ansight.app-tool.diagnostic", artifact.Source);
        Assert.Equal("open-file-descriptors.json", Assert.Single(artifact.Entries).Name);

        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
        await DrainSocketAsync(socket);
    }

    [Fact(Timeout = 60000)]
    public async Task CallAppToolAsync_DoesNotPersistToolBridgeTrafficAsSessionLogs()
    {
        using var environment = new TestEnvironment();
        var pairingConfig = environment.SeedPairingConfig("com.example.tools", "Tool Test App");
        using var runtime = environment.CreateRuntime();

        await runtime.StartAsync();

        var connectResponse = await SendConnectRequestAsync(pairingConfig);
        Assert.True(connectResponse.Accepted);
        Assert.NotNull(connectResponse.WebSocketPort);
        Assert.False(string.IsNullOrWhiteSpace(connectResponse.WebSocketToken));

        using var socket = new ClientWebSocket();
        var socketUri = new Uri(
            $"ws://127.0.0.1:{connectResponse.WebSocketPort}{connectResponse.WebSocketPath}?token={connectResponse.WebSocketToken}");
        await socket.ConnectAsync(socketUri, CancellationToken.None);

        string? sessionId = null;
        await TestWait.UntilAsync(
            () =>
            {
                var connectedSessions = runtime.AppTools.GetConnectedSessionIds();
                if (connectedSessions.Count != 1)
                {
                    return false;
                }

                sessionId = connectedSessions[0];
                return true;
            },
            because: "The paired app should appear as a connected tool session.");

        var queryTask = runtime.AppTools.QueryAsync(sessionId!);
        var queryRequest = await ReceiveJsonAsync(socket);
        Assert.Equal(ToolProtocolMessageTypes.QueryType, queryRequest["type"]?.GetValue<string>());
        var queryRequestId = queryRequest["id"]?.GetValue<string>();
        Assert.False(string.IsNullOrWhiteSpace(queryRequestId));
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
                            ["id"] = "demo.echo",
                            ["policy"] = "read"
                        },
                        new JsonObject
                        {
                            ["id"] = "demo.critical",
                            ["policy"] = "critical"
                        },
                        new JsonObject
                        {
                            ["id"] = "mapwork.open",
                            ["policy"] = "read",
                            ["runtime"] = new JsonObject
                            {
                                ["available"] = false,
                                ["reasonCode"] = "screen_not_registered",
                                ["reason"] = "No active MapWorkScreen is registered.",
                                ["requiredState"] = "MapWorkScreen registered"
                            }
                        }
                    }
                }
            });
        var queryResponse = await queryTask;
        Assert.True(queryResponse.Success);
        var catalogTools = queryResponse.Envelope!.Payload!["tools"]!.AsArray();
        var echoTool = catalogTools.Single(node => node!["id"]!.GetValue<string>() == "demo.echo")!.AsObject();
        Assert.True(echoTool["executable"]!.GetValue<bool>());
        Assert.Null(echoTool["denial"]);
        var criticalTool = catalogTools.Single(node => node!["id"]!.GetValue<string>() == "demo.critical")!.AsObject();
        Assert.False(criticalTool["executable"]!.GetValue<bool>());
        Assert.Equal("tool_policy_not_granted", criticalTool["denial"]!["code"]!.GetValue<string>());
        var unavailableTool = catalogTools.Single(node => node!["id"]!.GetValue<string>() == "mapwork.open")!.AsObject();
        Assert.False(unavailableTool["executable"]!.GetValue<bool>());
        Assert.Equal("screen_not_registered", unavailableTool["denial"]!["code"]!.GetValue<string>());

        var missingCall = await runtime.AppTools.CallAsync(sessionId!, "demo.missing", new JsonObject());
        Assert.False(missingCall.Success);
        Assert.Contains("not exposed by the last authenticated tool catalog", missingCall.Message, StringComparison.Ordinal);
        Assert.Contains("demo.missing", missingCall.Message, StringComparison.Ordinal);

        var criticalCall = await runtime.AppTools.CallAsync(sessionId!, "demo.critical", new JsonObject());
        Assert.False(criticalCall.Success);
        Assert.Contains("critical", criticalCall.Message, StringComparison.OrdinalIgnoreCase);

        var unavailableCall = await runtime.AppTools.CallAsync(sessionId!, "mapwork.open", new JsonObject());
        Assert.False(unavailableCall.Success);
        Assert.Contains("MapWorkScreen", unavailableCall.Message, StringComparison.Ordinal);

        var toolCallTask = runtime.AppTools.CallAsync(
            sessionId!,
            "demo.echo",
            new JsonObject
            {
                ["value"] = 42
            });

        var toolRequest = await ReceiveJsonAsync(socket);
        Assert.Equal(ToolProtocolMessageTypes.CallType, toolRequest["type"]?.GetValue<string>());
        Assert.Equal(sessionId, toolRequest["sessionId"]?.GetValue<string>());
        var requestId = toolRequest["id"]?.GetValue<string>();
        Assert.False(string.IsNullOrWhiteSpace(requestId));
        var requestPayload = toolRequest["payload"]!.AsObject();
        Assert.Equal("demo.echo", requestPayload["toolId"]?.GetValue<string>());

        await SendJsonAsync(
            socket,
            new JsonObject
            {
                ["type"] = ToolProtocolMessageTypes.ResultType,
                ["id"] = $"{requestId}.response",
                ["replyTo"] = requestId,
                ["sessionId"] = sessionId,
                ["payload"] = new JsonObject
                {
                    ["toolId"] = "demo.echo",
                    ["success"] = true,
                    ["result"] = new JsonObject
                    {
                        ["value"] = 42
                    }
                }
            });

        var response = await toolCallTask;
        Assert.True(response.Success);
        Assert.NotNull(response.Envelope);
        Assert.Equal(ToolProtocolMessageTypes.ResultType, response.Envelope!.Type);

        await Task.Delay(300);

        Assert.True(runtime.Sessions.TryGetSnapshot(sessionId!, out var snapshot));
        Assert.NotNull(snapshot);
        Assert.DoesNotContain(snapshot!.Logs, log => string.Equals(log.Tag, "Tools", StringComparison.Ordinal));
        Assert.DoesNotContain(snapshot.Logs, log => log.Message.Contains("Received tool result from app.", StringComparison.Ordinal));

        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
        await DrainSocketAsync(socket);
    }
}
