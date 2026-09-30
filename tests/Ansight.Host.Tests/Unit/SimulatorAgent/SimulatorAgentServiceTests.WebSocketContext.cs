using System.Text.Json.Nodes;
using Ansight.Host.Tests.TestSupport;

namespace Ansight.Host.Tests.Unit.SimulatorAgent;

public sealed partial class SimulatorAgentServiceTests
{

    [Fact]
    public async Task RunAsync_WebSocketDoesNotTrimAtTheLegacyItemLimit()
    {
        const int actionCount = 70;
        var turns = Enumerable.Range(0, actionCount)
            .Select(index => CreateFunctionTurn($"tap-{index}", "ansight_tap_ui",
                new JsonObject { ["screenX"] = 100 + index, ["screenY"] = 200 }))
            .Append(CompleteWebSocketTurn());
        var session = new FakeOpenAiSession(turns);
        using var service = CreateWebSocketService(session);

        var result = await service.RunAsync(new SimulatorAgentRunRequest("session-123", ["Tap each control."],
            MaximumTurnsPerInstruction: 80)
        {
            OpenAiProtocol = SimulatorAgentOpenAiProtocol.WebSocket,
            MaximumRoundTrips = 80,
            WebSocketOptions = new SimulatorAgentWebSocketOptions(64_000)
        });

        Assert.Equal(SimulatorAgentRunStatus.Succeeded, result.Status);
        Assert.True(session.Requests[^1].Input.Count > 120);
        Assert.All(session.Requests.Skip(1), static request => Assert.False(request.StartNewConversation));
        Assert.Contains(session.Requests[^1].Input.OfType<JsonObject>(),
            static item => item["call_id"]?.GetValue<string>() == "tap-0");
        Assert.All(session.Requests, static request => Assert.Equal(64_000, request.CompactThresholdTokens));
    }

    [Fact]
    public async Task RunAsync_ServerCompactionUpdatesRecoverySnapshotWithoutResettingChain()
    {
        var compacted = CreateFunctionTurn("tap-after-compaction", "ansight_tap_ui",
            new JsonObject { ["screenX"] = 200, ["screenY"] = 200 });
        var checkpoint = new JsonObject
        {
            ["type"] = "compaction", ["id"] = "cmp-1", ["encrypted_content"] = "opaque-checkpoint"
        };
        var reasoning = new JsonObject
        {
            ["type"] = "reasoning", ["id"] = "reason-1", ["encrypted_content"] = "opaque-reasoning",
            ["summary"] = new JsonArray()
        };
        compacted.Output.Insert(0, checkpoint);
        compacted.Output.Insert(1, reasoning);
        var session = new FakeOpenAiSession([
            CreateFunctionTurn("tap-before-compaction", "ansight_tap_ui",
                new JsonObject { ["screenX"] = 100, ["screenY"] = 200 }),
            compacted,
            CompleteWebSocketTurn()
        ]);
        using var service = CreateWebSocketService(session);

        var result = await service.RunAsync(new SimulatorAgentRunRequest("session-123", ["Tap both controls."])
        {
            OpenAiProtocol = SimulatorAgentOpenAiProtocol.WebSocket,
            CaptureTrace = true
        });

        Assert.Equal(SimulatorAgentRunStatus.Succeeded, result.Status);
        var finalRequest = session.Requests[2];
        Assert.False(finalRequest.StartNewConversation);
        Assert.True(JsonNode.DeepEquals(checkpoint, finalRequest.Input[0]));
        Assert.True(JsonNode.DeepEquals(reasoning, finalRequest.Input[1]));
        Assert.DoesNotContain(finalRequest.Input.OfType<JsonObject>(),
            static item => item["call_id"]?.GetValue<string>() == "tap-before-compaction");
        Assert.Contains(finalRequest.IncrementalInput!.OfType<JsonObject>(),
            static item => item["type"]?.GetValue<string>() == "function_call_output"
                && item["call_id"]?.GetValue<string>() == "tap-after-compaction");
        Assert.DoesNotContain(finalRequest.IncrementalInput!.OfType<JsonObject>(),
            static item => item["type"]?.GetValue<string>() is "compaction" or "reasoning");
        Assert.Contains(result.Audit.ToolCalls, static call => call.CallId == "tap-before-compaction");
    }

    [Fact]
    public async Task RunAsync_WebSocketCompletionGracePreservesToolDefinitions()
    {
        var session = new FakeOpenAiSession([
            CreateFunctionTurn("tap", "ansight_tap_ui", new JsonObject { ["screenX"] = 100, ["screenY"] = 200 }),
            CompleteWebSocketTurn()
        ]);
        using var service = CreateWebSocketService(session);

        var result = await service.RunAsync(new SimulatorAgentRunRequest("session-123", ["Tap the control."],
            MaximumTurnsPerInstruction: 1)
        {
            OpenAiProtocol = SimulatorAgentOpenAiProtocol.WebSocket
        });

        Assert.Equal(SimulatorAgentRunStatus.Succeeded, result.Status);
        Assert.False(session.Requests[0].CompletionOnly);
        Assert.True(session.Requests[1].CompletionOnly);
        Assert.True(JsonNode.DeepEquals(session.Requests[0].Tools, session.Requests[1].Tools));
        Assert.False(session.Requests[1].StartNewConversation);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunAsync_WebSocketWarmupIsExplicitAndUsesTheSamePrefix(bool enableWarmup)
    {
        var session = new FakeOpenAiSession([CompleteWebSocketTurn()]);
        using var service = CreateWebSocketService(session);

        var result = await service.RunAsync(new SimulatorAgentRunRequest("session-123", ["Verify the app."])
        {
            OpenAiProtocol = SimulatorAgentOpenAiProtocol.WebSocket,
            WebSocketOptions = new SimulatorAgentWebSocketOptions(64_000, enableWarmup)
        });

        Assert.Equal(SimulatorAgentRunStatus.Succeeded, result.Status);
        Assert.Equal(enableWarmup ? 1 : 0, session.WarmupRequests.Count);
        if (enableWarmup)
        {
            var warmup = Assert.Single(session.WarmupRequests);
            var generated = Assert.Single(session.Requests);
            Assert.Equal(generated.Instructions, warmup.Instructions);
            Assert.True(JsonNode.DeepEquals(generated.Tools, warmup.Tools));
            Assert.Empty(warmup.Input);
            Assert.Equal(generated.CompactThresholdTokens, warmup.CompactThresholdTokens);
        }
        Assert.Equal(new SimulatorAgentWebSocketOptions(64_000, enableWarmup), result.Audit.WebSocketOptions);
    }

    [Fact]
    public async Task RunAsync_RecordsTransportAndAttributesWarmupUsageSeparately()
    {
        var warmupTokens = new SimulatorAgentTokenUsage(100, 0, 100, 0, 100, 0);
        var warmupUsage = new SimulatorAgentModelPassUsage("warmup-1", "gpt-5.6-luna", null,
            DateTimeOffset.UtcNow, warmupTokens);
        var diagnostics = new SimulatorAgentTransportDiagnostics([
            new("warmup", "initial", 1, 512, 10),
            new("incremental", null, 1, 100, 20)
        ], 1)
        {
            WarmupTokens = warmupTokens,
            WarmupResponses = [warmupUsage]
        };
        var turn = CompleteWebSocketTurn() with { Transport = diagnostics };
        var session = new FakeOpenAiSession([turn]);
        using var service = CreateWebSocketService(session);

        var result = await service.RunAsync(new SimulatorAgentRunRequest("session-123", ["Verify the app."])
        {
            OpenAiProtocol = SimulatorAgentOpenAiProtocol.WebSocket
        });

        Assert.Equal(SimulatorAgentRunStatus.Succeeded, result.Status);
        Assert.Equal(diagnostics, Assert.Single(result.Audit.ModelPasses).Transport);
        Assert.Equal(turn.Tokens.Add(warmupTokens), result.Audit.Tokens);
        Assert.Contains(result.Audit.CreateModelPassUsages(), usage => usage == warmupUsage);
        Assert.Contains(result.Audit.CreateModelPassUsages(), usage => usage.ResponseId == turn.ResponseId
            && usage.Tokens == turn.Tokens);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunAsync_PreservesCompletedWarmupUsageWhenGenerationFailsOrRunIsCancelled(bool cancelAfterWarmup)
    {
        using var cancellation = new CancellationTokenSource();
        var session = new WarmupAccountingSession(cancelAfterWarmup ? cancellation : null);
        using var service = new SimulatorAgentService(new InMemoryEncryptedStorage(), new FakeOpenAiClient([]),
            new FakeToolGateway(), openAiWebSocketSessionFactory: new FakeOpenAiSessionFactory(session));
        service.SetDefaultModelAccessTokenForTesting("sk-local-test");

        var result = await service.RunAsync(new SimulatorAgentRunRequest("session-123", ["Verify the app."])
        {
            OpenAiProtocol = SimulatorAgentOpenAiProtocol.WebSocket,
            WebSocketOptions = new SimulatorAgentWebSocketOptions(EnableWarmup: true)
        }, cancellationToken: cancellation.Token);

        Assert.Equal(cancelAfterWarmup ? SimulatorAgentRunStatus.Cancelled : SimulatorAgentRunStatus.Failed, result.Status);
        Assert.Equal(100, result.Audit.Tokens.TotalTokens);
        Assert.Equal("warmup-1", Assert.Single(result.Audit.CreateModelPassUsages()).ResponseId);
        Assert.Single(result.Audit.WarmupResponses);
    }

    private sealed class CredentialWarmupSession : IOpenAiSession
    {
        public List<string> WarmupKeys { get; } = [];

        public async Task WarmupAsync(OpenAiRequest request, CancellationToken cancellationToken)
            => WarmupKeys.Add(await request.ResolveApiKeyAsync(cancellationToken));

        public Task<OpenAiTurn> CreateResponseAsync(OpenAiRequest request, CancellationToken cancellationToken)
            => Task.FromResult(CompleteWebSocketTurn());

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class WarmupAccountingSession(CancellationTokenSource? cancelAfterWarmup) : IOpenAiSession
    {
        public SimulatorAgentTransportDiagnostics? LastTransportDiagnostics { get; private set; }

        public Task WarmupAsync(OpenAiRequest request, CancellationToken cancellationToken)
        {
            var tokens = new SimulatorAgentTokenUsage(100, 0, 100, 0, 100, 0);
            LastTransportDiagnostics = new SimulatorAgentTransportDiagnostics([new("warmup", null, 1, 512, 10)], 0)
            {
                WarmupTokens = tokens,
                WarmupResponses = [new("warmup-1", request.Model, null, DateTimeOffset.UtcNow, tokens)]
            };
            cancelAfterWarmup?.Cancel();
            return Task.CompletedTask;
        }

        public Task<OpenAiTurn> CreateResponseAsync(OpenAiRequest request, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Generation rejected.");

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static SimulatorAgentService CreateWebSocketService(FakeOpenAiSession session)
    {
        var service = new SimulatorAgentService(new InMemoryEncryptedStorage(), new FakeOpenAiClient([]),
            new FakeToolGateway(), openAiWebSocketSessionFactory: new FakeOpenAiSessionFactory(session));
        service.SetDefaultModelAccessTokenForTesting("sk-local-test");
        return service;
    }

    private static OpenAiTurn CompleteWebSocketTurn()
        => CreateFunctionTurn("complete", "complete_instruction", new JsonObject
        {
            ["outcome"] = "succeeded", ["summary"] = "Done."
        });
}
