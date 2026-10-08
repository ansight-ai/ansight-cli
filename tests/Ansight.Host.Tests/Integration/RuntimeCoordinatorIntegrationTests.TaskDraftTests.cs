using System.Net.Http.Json;
using Ansight.Host.Tests.TestSupport;

namespace Ansight.Host.Tests.Integration;

public sealed partial class RuntimeCoordinatorIntegrationTests
{
    [Fact(Timeout = 60000)]
    public async Task TaskDraftTestEndpoints_CancelClearAndRetryWithoutCancellingTheDraftOrDeletingHistory()
    {
        using var environment = new TestEnvironment();
        const string appId = "com.example.draft-cancellation";
        var pairing = environment.SeedPairingConfig(appId, "Draft Cancellation App");
        await using var runtime = environment.CreateRuntime();
        Assert.True(runtime.Apps.Register(new AppRegistrationRequest(appId, "Draft Cancellation App", environment.RootPath)).IsSuccess);
        await runtime.StartAsync();
        var connection = await SendConnectRequestAsync(pairing);
        Assert.True(connection.Accepted);
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{connection.WebSocketPort}{connection.WebSocketPath}?token={connection.WebSocketToken}"), CancellationToken.None);
        string? sessionId = null;
        await TestWait.UntilAsync(() => (sessionId = runtime.AppTools.GetConnectedSessionIds().SingleOrDefault()) is not null);
        await using var catalog = new TaskDraftCatalogResponder(socket, sessionId!);
        await TestWait.UntilAsync(() => runtime.Sessions.TryGetSnapshot(sessionId!, out var snapshot) && snapshot?.AppToolCatalog is not null);

        var extractionId = Guid.NewGuid().ToString("N");
        var draftRoot = Path.Combine(environment.RootPath, "task-extraction-drafts", extractionId);
        var sourcePath = Path.Combine(draftRoot, "ansight", "tasks", "cancel-me.ts");
        Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
        const string source = """
            export const task = {
              "schemaVersion": 1, "appId": "com.example.draft-cancellation", "title": "Cancellation test",
              "description": "Test cancellation and retry.", "timeoutSeconds": 90,
              "inputSchema": {"type":"object","properties":{"wait":{"type":"boolean"}},"required":["wait"],"additionalProperties":false}
            };
            export default async function run({ input, expect }) {
              if (input.wait) await new Promise(resolve => setTimeout(resolve, 60000));
              expect(true, { id: "completed" }).toBe(true);
            }
            """;
        await File.WriteAllTextAsync(sourcePath, source);
        var now = DateTimeOffset.UtcNow;
        var saved = new LocalTaskExtractionSnapshot(
            "ansight.local-task-extraction/v1", extractionId, "ready", "Ready.", sessionId!, appId,
            environment.RootPath, now.AddSeconds(-1), now, "Cancellation test", "Test cancellation.",
            "hosted", "", false, now, now, [],
            new LocalTaskExtractionDraft("cancel-me", "Test cancellation.", source, "cancel-me", draftRoot, sourcePath, []),
            "idle", null, null, null, null);
        var explorer = await runtime.SessionReplays.StartExplorerAsync(new SessionExplorerStartRequest());
        Assert.True(explorer.IsSuccess, explorer.Message);
        using var client = new HttpClient { BaseAddress = explorer.ExplorerUrl };
        using var restored = await client.PostAsJsonAsync("api/task-extractions/restore-drafts", new { extractions = new[] { saved } });
        Assert.True(restored.IsSuccessStatusCode, await restored.Content.ReadAsStringAsync());
        var route = $"api/task-extractions/{extractionId}";

        using var started = await client.PostAsJsonAsync(route + "/test", new { sessionId, input = new { wait = true } });
        Assert.Equal(HttpStatusCode.Accepted, started.StatusCode);
        using var busyClear = await client.PostAsJsonAsync(route + "/clear-test", new { });
        Assert.Equal(HttpStatusCode.BadRequest, busyClear.StatusCode);
        await Task.Delay(250);
        using var cancelled = await client.PostAsJsonAsync(route + "/cancel-test", new { });
        Assert.Equal(HttpStatusCode.Accepted, cancelled.StatusCode);
        var result = await WaitForDraftTestResultAsync(client, route);
        Assert.Equal("cancelled", result.TestStatus);
        Assert.Equal("ready", result.Status);
        Assert.Equal(source, result.Draft!.Source);
        Assert.NotNull(result.TestResult?.RunId);
        var cancelledRunId = result.TestResult!.RunId;

        using var cleared = await client.PostAsJsonAsync(route + "/clear-test", new { });
        var clearState = await cleared.Content.ReadFromJsonAsync<LocalTaskExtractionSnapshot>();
        Assert.Equal("idle", clearState!.TestStatus);
        Assert.Null(clearState.TestResult);
        Assert.Null(clearState.TestMessage);
        using var rejected = await client.PostAsJsonAsync(route + "/test", new { sessionId = "missing", input = new { wait = false } });
        Assert.Equal(HttpStatusCode.Accepted, rejected.StatusCode);
        var failed = await WaitForDraftTestResultAsync(client, route);
        Assert.Equal("failed", failed.TestStatus);
        Assert.NotNull(failed.TestMessage);
        using var clearFailed = await client.PostAsJsonAsync(route + "/clear-test", new { });
        var clearedFailure = await clearFailed.Content.ReadFromJsonAsync<LocalTaskExtractionSnapshot>();
        Assert.Equal("idle", clearedFailure!.TestStatus);
        Assert.Null(clearedFailure.TestMessage);
        using var restarted = await client.PostAsJsonAsync(route + "/test", new { sessionId, input = new { wait = false } });
        Assert.Equal(HttpStatusCode.Accepted, restarted.StatusCode);
        result = await WaitForDraftTestResultAsync(client, route);
        Assert.Equal("passed", result.TestStatus);
        Assert.True(Assert.Single(result.TestResult!.Assertions).Passed);
        using var clearPassed = await client.PostAsJsonAsync(route + "/clear-test", new { });
        Assert.Equal(HttpStatusCode.OK, clearPassed.StatusCode);
        var history = await File.ReadAllTextAsync(Path.Combine(environment.ApplicationPaths.ApplicationDataPath, "automation", "task-runs.jsonl"));
        Assert.Contains(cancelledRunId, history);
        Assert.Contains(result.TestResult.RunId, history);
        Assert.Equal(source, await File.ReadAllTextAsync(sourcePath));
    }

    private static async Task<LocalTaskExtractionSnapshot> WaitForDraftTestResultAsync(HttpClient client, string route)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            var state = (await client.GetFromJsonAsync<LocalTaskExtractionSnapshot>(route, timeout.Token))!;
            if (state.TestStatus is not ("running" or "cancelling")) return state;
            await Task.Delay(50, timeout.Token);
        }
    }

    private sealed class TaskDraftCatalogResponder : IAsyncDisposable
    {
        private readonly CancellationTokenSource lifetime = new();
        private readonly Task responses;

        public TaskDraftCatalogResponder(ClientWebSocket socket, string sessionId)
            => responses = RespondAsync(socket, sessionId);

        private async Task RespondAsync(ClientWebSocket socket, string sessionId)
        {
            try
            {
                var buffer = new byte[8192];
                while (!lifetime.IsCancellationRequested)
                {
                    using var message = new MemoryStream();
                    WebSocketReceiveResult received;
                    do
                    {
                        received = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), lifetime.Token);
                        if (received.MessageType == WebSocketMessageType.Close) return;
                        message.Write(buffer, 0, received.Count);
                    } while (!received.EndOfMessage);
                    var request = JsonNode.Parse(message.ToArray())!;
                    Assert.Equal("tool.query", request["type"]!.GetValue<string>());
                    await SendJsonAsync(socket, new JsonObject
                    {
                        ["type"] = "tool.catalog", ["id"] = Guid.NewGuid().ToString("N"),
                        ["replyTo"] = request["id"]!.GetValue<string>(), ["sessionId"] = sessionId,
                        ["payload"] = new JsonObject { ["tools"] = new JsonArray() }
                    });
                }
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        }

        public async ValueTask DisposeAsync()
        {
            await lifetime.CancelAsync();
            await responses;
            lifetime.Dispose();
        }
    }
}
