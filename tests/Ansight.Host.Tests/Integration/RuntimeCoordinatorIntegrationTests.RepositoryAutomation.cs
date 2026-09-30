using Ansight.Host.Tests.TestSupport;
using Ansight.Tools;

namespace Ansight.Host.Tests.Integration;

public sealed partial class RuntimeCoordinatorIntegrationTests
{
    [Fact(Timeout = 60000)]
    public async Task RepositoryAutomation_NewSessionQueriesCatalogBeforeDispatchingReturnedAppTool()
    {
        using var environment = new TestEnvironment(webSocketSessionPortCount: 2);
        const string appId = "com.example.repository-automation";
        var repositoryPath = CreateRepositoryAutomationFixture(environment, appId);
        var pairingConfig = environment.SeedPairingConfig(appId, "Repository Automation App");
        using var runtime = environment.CreateRuntime(enableRepositoryAutomations: true);
        var completedRun = new TaskCompletionSource<AutomationRunCompletedEvent>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        runtime.AutomationRunCompleted += (_, run) =>
        {
            if (string.Equals(run.EventId, "automation-event-1", StringComparison.Ordinal))
            {
                completedRun.TrySetResult(run);
            }
        };

        await runtime.StartAsync();
        var connection = runtime.RepositoryAutomations.Connect(appId, repositoryPath);
        Assert.True(connection.IsConnected, string.Join(Environment.NewLine, connection.Warnings));

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
            because: "The newly paired app should appear as a connected tool session.");

        await SendRepositoryAutomationEventAsync(socket, "automation-event-1");

        var catalogRequest = await ReceiveJsonAsync(socket);
        Assert.Equal(ToolProtocolMessageTypes.QueryType, catalogRequest["type"]?.GetValue<string>());
        Assert.Equal(sessionId, catalogRequest["sessionId"]?.GetValue<string>());
        Assert.Equal("index", catalogRequest["payload"]?["detail"]?.GetValue<string>());
        Assert.Null(catalogRequest["payload"]?["ifRevision"]);
        await SendCatalogResponseAsync(socket, catalogRequest, sessionId!, policy: "read");

        var toolRequest = await ReceiveJsonAsync(socket);
        // Session evidence capture and the automation may each request the catalog.
        // Both must finish discovery before the automation dispatches its tool.
        if (toolRequest["type"]?.GetValue<string>() == ToolProtocolMessageTypes.QueryType)
        {
            Assert.Equal("index", toolRequest["payload"]?["detail"]?.GetValue<string>());
            Assert.Equal(sessionId, toolRequest["sessionId"]?.GetValue<string>());
            await SendCatalogResponseAsync(socket, toolRequest, sessionId!, policy: "read");
            toolRequest = await ReceiveJsonAsync(socket);
        }
        Assert.Equal(ToolProtocolMessageTypes.CallType, toolRequest["type"]?.GetValue<string>());
        Assert.Equal(sessionId, toolRequest["sessionId"]?.GetValue<string>());
        Assert.Equal("artifacts.request", toolRequest["payload"]?["toolId"]?.GetValue<string>());
        Assert.Equal(
            "redpoint.3d_player",
            toolRequest["payload"]?["arguments"]?["providerId"]?.GetValue<string>());
        await SendToolResultAsync(socket, toolRequest, sessionId!);

        var run = await completedRun.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(AutomationRunStatus.Succeeded, run.Status);
        Assert.Equal("artifacts.request", run.Output?["result"]?["toolId"]?.GetValue<string>());
        Assert.Equal(ToolProtocolMessageTypes.ResultType, run.Output?["result"]?["responseType"]?.GetValue<string>());

        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
        await DrainSocketAsync(socket);
    }

    [Fact(Timeout = 60000)]
    public async Task RepositoryAutomation_ToolNotFoundRefreshesCatalogAndRetriesOnce()
    {
        using var environment = new TestEnvironment(webSocketSessionPortCount: 2);
        const string appId = "com.example.repository-automation-refresh";
        var repositoryPath = CreateRepositoryAutomationFixture(environment, appId);
        var pairingConfig = environment.SeedPairingConfig(appId, "Repository Automation Refresh App");
        using var runtime = environment.CreateRuntime(enableRepositoryAutomations: true);
        var completedRun = new TaskCompletionSource<AutomationRunCompletedEvent>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        runtime.AutomationRunCompleted += (_, run) =>
        {
            if (string.Equals(run.EventId, "automation-event-refresh", StringComparison.Ordinal))
            {
                completedRun.TrySetResult(run);
            }
        };

        await runtime.StartAsync();
        var connection = runtime.RepositoryAutomations.Connect(appId, repositoryPath);
        Assert.True(connection.IsConnected, string.Join(Environment.NewLine, connection.Warnings));

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
            because: "The newly paired app should appear as a connected tool session.");

        await SendRepositoryAutomationEventAsync(socket, "automation-event-refresh");

        var initialIndexRequest = await ReceiveJsonAsync(socket);
        Assert.Equal(ToolProtocolMessageTypes.QueryType, initialIndexRequest["type"]?.GetValue<string>());
        Assert.Equal("index", initialIndexRequest["payload"]?["detail"]?.GetValue<string>());
        Assert.Null(initialIndexRequest["payload"]?["ifRevision"]);
        await SendVersionedIndexResponseAsync(
            socket,
            initialIndexRequest,
            sessionId!,
            catalogRevision: "catalog-1",
            definitionRevision: "definition-1");

        var initialDefinitionsRequest = await ReceiveJsonAsync(socket);
        Assert.Equal("definitions", initialDefinitionsRequest["payload"]?["detail"]?.GetValue<string>());
        await SendVersionedDefinitionsResponseAsync(
            socket,
            initialDefinitionsRequest,
            sessionId!,
            catalogRevision: "catalog-1",
            definitionRevision: "definition-1");

        var initialCallRequest = await ReceiveJsonAsync(socket);
        Assert.Equal(ToolProtocolMessageTypes.CallType, initialCallRequest["type"]?.GetValue<string>());
        await SendToolErrorAsync(
            socket,
            initialCallRequest,
            sessionId!,
            AppToolCallRecovery.ToolNotFoundErrorCode);

        var refreshedIndexRequest = await ReceiveJsonAsync(socket);
        Assert.Equal(ToolProtocolMessageTypes.QueryType, refreshedIndexRequest["type"]?.GetValue<string>());
        Assert.Equal("index", refreshedIndexRequest["payload"]?["detail"]?.GetValue<string>());
        Assert.Null(refreshedIndexRequest["payload"]?["ifRevision"]);
        Assert.Null(refreshedIndexRequest["payload"]?["ifAvailabilityRevision"]);
        await SendVersionedIndexResponseAsync(
            socket,
            refreshedIndexRequest,
            sessionId!,
            catalogRevision: "catalog-2",
            definitionRevision: "definition-2");

        var refreshedDefinitionsRequest = await ReceiveJsonAsync(socket);
        Assert.Equal("definitions", refreshedDefinitionsRequest["payload"]?["detail"]?.GetValue<string>());
        await SendVersionedDefinitionsResponseAsync(
            socket,
            refreshedDefinitionsRequest,
            sessionId!,
            catalogRevision: "catalog-2",
            definitionRevision: "definition-2");

        var retryCallRequest = await ReceiveJsonAsync(socket);
        Assert.Equal(ToolProtocolMessageTypes.CallType, retryCallRequest["type"]?.GetValue<string>());
        Assert.NotEqual(
            initialCallRequest["id"]?.GetValue<string>(),
            retryCallRequest["id"]?.GetValue<string>());
        Assert.Equal(
            "artifacts.request",
            retryCallRequest["payload"]?["toolId"]?.GetValue<string>());
        await SendToolResultAsync(socket, retryCallRequest, sessionId!);

        var run = await completedRun.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(AutomationRunStatus.Succeeded, run.Status);
        Assert.Equal(ToolProtocolMessageTypes.ResultType, run.Output?["result"]?["responseType"]?.GetValue<string>());

        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
        await DrainSocketAsync(socket);
    }

    [Fact(Timeout = 60000)]
    public async Task RepositoryAutomation_PolicyDeniedToolIsNotDispatchedAfterCatalogQuery()
    {
        using var environment = new TestEnvironment(webSocketSessionPortCount: 2);
        const string appId = "com.example.repository-automation-denied";
        var repositoryPath = CreateRepositoryAutomationFixture(environment, appId);
        var pairingConfig = environment.SeedPairingConfig(appId, "Denied Repository Automation App");
        using var runtime = environment.CreateRuntime(enableRepositoryAutomations: true);
        var completedRun = new TaskCompletionSource<AutomationRunCompletedEvent>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        runtime.AutomationRunCompleted += (_, run) =>
        {
            if (string.Equals(run.EventId, "automation-event-denied", StringComparison.Ordinal))
            {
                completedRun.TrySetResult(run);
            }
        };

        await runtime.StartAsync();
        var connection = runtime.RepositoryAutomations.Connect(appId, repositoryPath);
        Assert.True(connection.IsConnected, string.Join(Environment.NewLine, connection.Warnings));

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
            because: "The newly paired app should appear as a connected tool session.");

        await SendRepositoryAutomationEventAsync(socket, "automation-event-denied");

        var catalogRequest = await ReceiveJsonAsync(socket);
        Assert.Equal(ToolProtocolMessageTypes.QueryType, catalogRequest["type"]?.GetValue<string>());
        await SendCatalogResponseAsync(socket, catalogRequest, sessionId!, policy: "critical");

        var run = await completedRun.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(AutomationRunStatus.Rejected, run.Status);
        Assert.Contains("app.tool:artifacts.request", run.Message, StringComparison.Ordinal);
        Assert.Equal("capability_unavailable", run.Output?["result"]?["code"]?.GetValue<string>());

        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
        await DrainSocketAsync(socket);
    }

    private static string CreateRepositoryAutomationFixture(TestEnvironment environment, string appId)
    {
        var repositoryPath = Path.Combine(environment.RootPath, "repository-automation");
        var triggerDirectory = Path.Combine(repositoryPath, "ansight", "triggers");
        Directory.CreateDirectory(triggerDirectory);
        File.WriteAllText(
            Path.Combine(triggerDirectory, "capture-asset.ts"),
            $$"""
            export const trigger = {
              "appId": "{{appId}}",
              "eventKind": "app.event",
              "functionTimeoutMs": 100,
              "actionTimeoutSeconds": 10,
              "conditions": [
                {
                  "field": "payload.label",
                  "operator": "equals",
                  "value": "redpoint.guide3d.load.completed"
                }
              ]
            };

            export default async function captureAsset({ app }) {
              return app.artifacts.request({
                providerId: "redpoint.3d_player",
                artifactId: "uld_asset"
              });
            }
            """);
        return repositoryPath;
    }

    private static Task SendRepositoryAutomationEventAsync(ClientWebSocket socket, string eventId)
        => SendJsonAsync(socket, new JsonObject
        {
            ["type"] = "CLIENT_EVENTS",
            ["events"] = new JsonArray
            {
                new JsonObject
                {
                    ["id"] = eventId,
                    ["label"] = "redpoint.guide3d.load.completed",
                    ["eventType"] = "Info",
                    ["details"] = "siuranella-est-nord",
                    ["capturedAtUtc"] = "2026-09-03T02:13:09.159Z",
                    ["channel"] = 4
                }
            }
        });

    private static Task SendCatalogResponseAsync(
        ClientWebSocket socket,
        JsonObject request,
        string sessionId,
        string policy)
    {
        var requestId = request["id"]?.GetValue<string>();
        Assert.False(string.IsNullOrWhiteSpace(requestId));
        return SendJsonAsync(socket, new JsonObject
        {
            ["type"] = ToolProtocolMessageTypes.CatalogType,
            ["id"] = $"{requestId}.response",
            ["replyTo"] = requestId,
            ["sessionId"] = sessionId,
            ["payload"] = new JsonObject
            {
                ["tools"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["id"] = "artifacts.request",
                        ["policy"] = policy,
                        ["argumentsSchema"] = new JsonObject
                        {
                            ["type"] = "object"
                        }
                    }
                }
            }
        });
    }

    private static Task SendToolResultAsync(
        ClientWebSocket socket,
        JsonObject request,
        string sessionId)
    {
        var requestId = request["id"]?.GetValue<string>();
        Assert.False(string.IsNullOrWhiteSpace(requestId));
        return SendJsonAsync(socket, new JsonObject
        {
            ["type"] = ToolProtocolMessageTypes.ResultType,
            ["id"] = $"{requestId}.response",
            ["replyTo"] = requestId,
            ["sessionId"] = sessionId,
            ["payload"] = new JsonObject
            {
                ["toolId"] = "artifacts.request",
                ["success"] = true,
                ["result"] = new JsonObject
                {
                    ["snapshotId"] = "app-artifact-regression"
                }
            }
        });
    }

    private static Task SendVersionedIndexResponseAsync(
        ClientWebSocket socket,
        JsonObject request,
        string sessionId,
        string catalogRevision,
        string definitionRevision)
    {
        var requestId = request["id"]?.GetValue<string>();
        Assert.False(string.IsNullOrWhiteSpace(requestId));
        return SendJsonAsync(socket, new JsonObject
        {
            ["type"] = ToolProtocolMessageTypes.CatalogType,
            ["id"] = $"{requestId}.response",
            ["replyTo"] = requestId,
            ["sessionId"] = sessionId,
            ["payload"] = new JsonObject
            {
                ["schema"] = "ansight.tool-catalog.v3",
                ["revision"] = catalogRevision,
                ["availabilityRevision"] = $"{catalogRevision}-availability",
                ["detail"] = "index",
                ["tools"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["id"] = "artifacts.request",
                        ["policy"] = "read",
                        ["definitionRevision"] = definitionRevision
                    }
                }
            }
        });
    }

    private static Task SendVersionedDefinitionsResponseAsync(
        ClientWebSocket socket,
        JsonObject request,
        string sessionId,
        string catalogRevision,
        string definitionRevision)
    {
        var requestId = request["id"]?.GetValue<string>();
        Assert.False(string.IsNullOrWhiteSpace(requestId));
        return SendJsonAsync(socket, new JsonObject
        {
            ["type"] = ToolProtocolMessageTypes.CatalogType,
            ["id"] = $"{requestId}.response",
            ["replyTo"] = requestId,
            ["sessionId"] = sessionId,
            ["payload"] = new JsonObject
            {
                ["schema"] = "ansight.tool-catalog.v3",
                ["revision"] = catalogRevision,
                ["detail"] = "definitions",
                ["tools"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["id"] = "artifacts.request",
                        ["policy"] = "read",
                        ["definitionRevision"] = definitionRevision,
                        ["argumentsSchema"] = new JsonObject { ["type"] = "object" },
                        ["resultSchema"] = new JsonObject { ["type"] = "object" }
                    }
                }
            }
        });
    }

    private static Task SendToolErrorAsync(
        ClientWebSocket socket,
        JsonObject request,
        string sessionId,
        string code)
    {
        var requestId = request["id"]?.GetValue<string>();
        Assert.False(string.IsNullOrWhiteSpace(requestId));
        return SendJsonAsync(socket, new JsonObject
        {
            ["type"] = ToolProtocolMessageTypes.ErrorType,
            ["id"] = $"{requestId}.response",
            ["replyTo"] = requestId,
            ["sessionId"] = sessionId,
            ["payload"] = new JsonObject
            {
                ["code"] = code,
                ["message"] = "The requested tool was not found.",
                ["retryable"] = true
            }
        });
    }
}
