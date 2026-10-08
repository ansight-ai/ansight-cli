using System.Text.Json.Nodes;

namespace Ansight.Host.Tests.Unit.SimulatorAgent;

public sealed partial class SimulatorAgentServiceTests
{
    [Fact]
    public async Task RunAsync_WebSocketKeepsTaskSchemasAfterTheStableCoreAndExecutesTypedShortcuts()
    {
        const string taskToolName = "ansight_task_open_account";
        var gateway = new FakeToolGateway
        {
            RepositoryTaskShortcuts = [CreateRepositoryTaskShortcut(taskToolName, "open-account", "Open account")],
            Result = new ToolCallResult(false,
                "{\"isError\":false,\"result\":{\"taskId\":\"open-account\",\"status\":\"Passed\"}}", "Task passed.")
        };
        AddToolLoadingDefinitions(gateway, "ansight_find_ui", "ansight_tap_ui");
        var session = new FakeOpenAiSession([
            CreateFunctionTurn("task", taskToolName, new JsonObject()), CompleteWebSocketTurn()
        ]);
        using var service = CreateToolLoadingService(session, gateway);

        var result = await service.RunAsync(ToolLoadingRequest("Open account."));

        Assert.Equal(SimulatorAgentRunStatus.Succeeded, result.Status);
        Assert.Equal(new[]
        {
            "ansight_get_live_visual_tree", "ansight_list_tasks", "ansight_run_task",
            "complete_instruction", "ansight_load_tools"
        }, ToolNames(session.Requests[0].Tools));
        Assert.All(session.Requests, request => Assert.True(JsonNode.DeepEquals(session.Requests[0].Tools, request.Tools)));
        var declaration = Assert.Single(AdditionalToolItems(session.Requests[0].Input));
        Assert.Equal("developer", declaration["role"]?.GetValue<string>());
        Assert.Equal(new[] { "ansight_declare_uncovered_step", taskToolName }, ToolNames(declaration["tools"]!.AsArray()));
        Assert.Same(session.Requests[0].Input[0], declaration);
        Assert.Empty(AdditionalToolItems(session.Requests[1].IncrementalInput!));
        var dispatched = Assert.Single(gateway.Calls);
        Assert.Equal("ansight_run_task", dispatched.ToolName);
        Assert.Equal("open-account", dispatched.Arguments["taskId"]?.GetValue<string>());
    }

    [Fact]
    public async Task RunAsync_LoadsManualToolsOnceAndBlocksCallsBeforeLoadingWithoutDispatchingThem()
    {
        const string fullOutput = "{\"isError\":false,\"result\":{\"matches\":[],\"internalHistory\":[\"full retained evidence\"]}}";
        const string modelOutput = "{\"isError\":false,\"result\":{\"matches\":[]}}";
        var gateway = new FakeToolGateway
        {
            Result = new ToolCallResult(false, fullOutput, "Observed the control.") { ModelOutput = modelOutput }
        };
        AddToolLoadingDefinitions(gateway, "ansight_find_ui", "ansight_tap_ui", "ansight_swipe_ui");
        var session = new FakeOpenAiSession([
            CreateFunctionTurn("hidden", "ansight_find_ui", new JsonObject { ["automationId"] = "account" }),
            LoadManualToolsTurn("load"), LoadManualToolsTurn("load-again"),
            CreateFunctionTurn("visible", "ansight_find_ui", new JsonObject { ["automationId"] = "account" }),
            CompleteWebSocketTurn()
        ]);
        using var service = CreateToolLoadingService(session, gateway);

        var result = await service.RunAsync(ToolLoadingRequest("Inspect account.") with { CaptureTrace = true });

        Assert.Equal(SimulatorAgentRunStatus.Succeeded, result.Status);
        var blocked = Assert.Single(result.Audit.ToolCalls, call => call.CallId == "hidden");
        Assert.True(blocked.IsError);
        Assert.Contains("toolLoadingRequired", blocked.Result.Content, StringComparison.Ordinal);
        Assert.Single(gateway.Calls);
        Assert.Equal("ansight_find_ui", gateway.Calls[0].ToolName);
        Assert.All(session.Requests, request => Assert.True(JsonNode.DeepEquals(session.Requests[0].Tools, request.Tools)));
        var loadedInput = session.Requests[2].IncrementalInput!;
        var declaration = Assert.Single(AdditionalToolItems(loadedInput));
        Assert.Equal(new[] { "ansight_find_ui", "ansight_tap_ui", AgentUiBatch.ToolName }, ToolNames(declaration["tools"]!.AsArray()));
        var declarationIndex = loadedInput.IndexOf(declaration);
        Assert.Equal("function_call_output", loadedInput[declarationIndex - 1]?["type"]?.GetValue<string>());
        Assert.Equal("developer", loadedInput[declarationIndex + 1]?["role"]?.GetValue<string>());
        Assert.Empty(AdditionalToolItems(session.Requests[3].IncrementalInput!));
        Assert.DoesNotContain(session.Requests[3].IncrementalInput!.OfType<JsonObject>(),
            item => item["role"]?.GetValue<string>() == "developer");
        Assert.Single(AdditionalToolItems(session.Requests[^1].Input));
        var visibleOutput = Assert.Single(session.Requests[^1].Input.OfType<JsonObject>(),
            item => item["call_id"]?.GetValue<string>() == "visible" && item["type"]?.GetValue<string>() == "function_call_output");
        Assert.Equal(modelOutput, visibleOutput["output"]?.GetValue<string>());
        Assert.Equal(fullOutput, Assert.Single(result.Audit.ToolCalls, call => call.CallId == "visible").Result.Content);
    }

    [Fact]
    public async Task RunAsync_CompactionRetainsToolDeclarationsAndGuidanceOnlyInTheRecoveryHistory()
    {
        var checkpoint = new JsonObject
        {
            ["type"] = "compaction", ["id"] = "compact-loaded-tools", ["encrypted_content"] = "opaque-checkpoint"
        };
        var observation = CreateFunctionTurn("find-after-compaction", "ansight_find_ui",
            new JsonObject { ["automationId"] = "account" });
        observation.Output.Insert(0, checkpoint.DeepClone());
        var gateway = new FakeToolGateway
        {
            RepositoryTaskShortcuts = [CreateRepositoryTaskShortcut("ansight_task_account", "account", "Account")]
        };
        AddToolLoadingDefinitions(gateway, "ansight_find_ui");
        var session = new FakeOpenAiSession([
            LoadManualToolsTurn("load"), observation, LoadManualToolsTurn("load-again"), CompleteWebSocketTurn()
        ]);
        using var service = CreateToolLoadingService(session, gateway);

        var result = await service.RunAsync(ToolLoadingRequest("Inspect account."));

        Assert.Equal(SimulatorAgentRunStatus.Succeeded, result.Status);
        var loadedDeclaration = Assert.Single(AdditionalToolItems(session.Requests[1].IncrementalInput!));
        var guidance = Assert.Single(session.Requests[1].IncrementalInput!.OfType<JsonObject>(),
            item => item["role"]?.GetValue<string>() == "developer" && item["type"] is null);
        foreach (var request in session.Requests.Skip(2))
        {
            Assert.False(request.StartNewConversation);
            var declarations = AdditionalToolItems(request.Input).ToArray();
            Assert.Equal(2, declarations.Length);
            Assert.True(JsonNode.DeepEquals(loadedDeclaration, declarations[1]));
            var retainedGuidance = Assert.Single(request.Input.OfType<JsonObject>(),
                item => item["role"]?.GetValue<string>() == "developer" && item["type"] is null);
            Assert.True(JsonNode.DeepEquals(guidance, retainedGuidance));
            var checkpointItem = Assert.Single(request.Input.OfType<JsonObject>(),
                item => item["type"]?.GetValue<string>() == "compaction");
            Assert.True(JsonNode.DeepEquals(checkpoint, checkpointItem));
            Assert.True(request.Input.IndexOf(retainedGuidance) < request.Input.IndexOf(checkpointItem));
            Assert.Empty(AdditionalToolItems(request.IncrementalInput!));
            Assert.DoesNotContain(request.IncrementalInput!.OfType<JsonObject>(),
                item => item["role"]?.GetValue<string>() == "developer" || item["type"]?.GetValue<string>() == "compaction");
        }
        Assert.Single(gateway.Calls);
    }

    [Fact]
    public async Task RunAsync_HttpFallbackExpandsTheCatalogAndRemovesLoadedDeclarations()
    {
        var gateway = new FakeToolGateway();
        AddToolLoadingDefinitions(gateway, "ansight_find_ui", "ansight_tap_ui", "ansight_swipe_ui");
        var beforeFailure = new FakeOpenAiSession([
            LoadManualToolsTurn("load"),
            CreateFunctionTurn("find", "ansight_find_ui", new JsonObject { ["automationId"] = "account" })
        ]);
        var httpClient = new FakeOpenAiClient([CompleteWebSocketTurn()]);
        using var service = CreateToolLoadingService(new FailAfterToolLoadingSession(beforeFailure, 2), gateway, httpClient);

        var result = await service.RunAsync(ToolLoadingRequest("Inspect account.") with
        {
            OpenAiProtocol = SimulatorAgentOpenAiProtocol.Auto
        });

        Assert.Equal(SimulatorAgentRunStatus.Succeeded, result.Status);
        Assert.Equal("http-fallback", result.Audit.OpenAiProtocol);
        Assert.Single(AdditionalToolItems(beforeFailure.Requests[1].Input));
        var httpInput = Assert.Single(httpClient.Inputs);
        Assert.Empty(AdditionalToolItems(httpInput));
        var httpTools = ToolNames(Assert.Single(httpClient.Tools));
        Assert.Contains("ansight_find_ui", httpTools);
        Assert.Contains("ansight_tap_ui", httpTools);
        Assert.Contains("ansight_swipe_ui", httpTools);
        Assert.DoesNotContain("ansight_load_tools", httpTools);
        Assert.Contains(httpInput.OfType<JsonObject>(),
            item => item["type"]?.GetValue<string>() == "function_call_output" && item["call_id"]?.GetValue<string>() == "find");
        Assert.Contains(AgentToolCatalog.ReadGuidance("manual-ui"), httpClient.Requests[0].Instructions, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_NewCapabilitiesAfterCompactionAppearOnceAtTheirOriginalRecoveryPosition()
    {
        var firstCheckpoint = new JsonObject
        {
            ["type"] = "compaction", ["id"] = "checkpoint-before-loading", ["encrypted_content"] = "first-checkpoint"
        };
        var firstLoad = LoadManualToolsTurn("load-manual");
        firstLoad.Output.Insert(0, firstCheckpoint.DeepClone());
        var secondCheckpoint = new JsonObject
        {
            ["type"] = "compaction", ["id"] = "checkpoint-after-loading", ["encrypted_content"] = "second-checkpoint"
        };
        var finalObservation = CreateFunctionTurn("find-after-second-checkpoint", "ansight_find_ui",
            new JsonObject { ["automationId"] = "account" });
        finalObservation.Output.Insert(0, secondCheckpoint.DeepClone());
        var gateway = new FakeToolGateway();
        AddToolLoadingDefinitions(gateway, "ansight_find_ui", "ansight_swipe_ui");
        var session = new FakeOpenAiSession([
            firstLoad,
            CreateFunctionTurn("find", "ansight_find_ui", new JsonObject { ["automationId"] = "account" }),
            CreateFunctionTurn("load-gestures", "ansight_load_tools", new JsonObject { ["bundle"] = "gestures" }),
            CreateFunctionTurn("swipe", "ansight_swipe_ui", new JsonObject { ["orientation"] = "up" }),
            finalObservation,
            CompleteWebSocketTurn()
        ]);
        using var service = CreateToolLoadingService(session, gateway);

        var result = await service.RunAsync(ToolLoadingRequest("Inspect and swipe the account page."));

        Assert.Equal(SimulatorAgentRunStatus.Succeeded, result.Status);
        // Input is the complete recovery snapshot used by the WebSocket session after cache loss.
        foreach (var request in session.Requests.Skip(1))
        {
            var declarations = AdditionalToolItems(request.Input).ToArray();
            var loadedNames = declarations.SelectMany(item => ToolNames(item["tools"]!.AsArray())).ToArray();
            Assert.Equal(loadedNames.Length, loadedNames.Distinct(StringComparer.Ordinal).Count());
            Assert.False(request.StartNewConversation);
        }
        var afterOrdinaryResponse = session.Requests[2].Input;
        var originalDeclaration = Assert.Single(AdditionalToolItems(afterOrdinaryResponse));
        Assert.True(JsonNode.DeepEquals(firstCheckpoint, afterOrdinaryResponse[0]));
        Assert.True(afterOrdinaryResponse.IndexOf(originalDeclaration) > 0);
        var afterGestureResponse = session.Requests[4];
        Assert.Equal(2, AdditionalToolItems(afterGestureResponse.Input).Count());
        Assert.True(JsonNode.DeepEquals(firstCheckpoint, afterGestureResponse.Input[0]));
        Assert.Empty(AdditionalToolItems(afterGestureResponse.IncrementalInput!));
        var afterNewCheckpoint = session.Requests[^1];
        var checkpointItem = Assert.Single(afterNewCheckpoint.Input.OfType<JsonObject>(),
            item => item["type"]?.GetValue<string>() == "compaction");
        Assert.True(JsonNode.DeepEquals(secondCheckpoint, checkpointItem));
        Assert.All(AdditionalToolItems(afterNewCheckpoint.Input),
            declaration => Assert.True(afterNewCheckpoint.Input.IndexOf(declaration) < afterNewCheckpoint.Input.IndexOf(checkpointItem)));
        Assert.Empty(AdditionalToolItems(afterNewCheckpoint.IncrementalInput!));
    }

    [Fact]
    public async Task RunAsync_HttpFallbackTrimsCompleteToolPairsWithoutDiscardingTheCheckpointAfterCapabilityGuidance()
    {
        const int actionCount = 65;
        var checkpoint = new JsonObject
        {
            ["type"] = "compaction", ["id"] = "checkpoint-before-long-tail", ["encrypted_content"] = "retained-state"
        };
        var actions = Enumerable.Range(0, actionCount).Select(index =>
            CreateFunctionTurn($"tap-{index}", "ansight_tap_ui", new JsonObject { ["automationId"] = $"control-{index}" })).ToArray();
        actions[0].Output.Insert(0, checkpoint.DeepClone());
        var gateway = new FakeToolGateway();
        AddToolLoadingDefinitions(gateway, "ansight_tap_ui");
        var beforeFailure = new FakeOpenAiSession(new[] { LoadManualToolsTurn("load") }.Concat(actions));
        var httpClient = new FakeOpenAiClient([CompleteWebSocketTurn()]);
        using var service = CreateToolLoadingService(
            new FailAfterToolLoadingSession(beforeFailure, actionCount + 1), gateway, httpClient);

        var result = await service.RunAsync(ToolLoadingRequest("Tap the requested controls.") with
        {
            OpenAiProtocol = SimulatorAgentOpenAiProtocol.Auto, MaximumTurnsPerInstruction = 80, MaximumRoundTrips = 80
        });

        Assert.Equal(SimulatorAgentRunStatus.Succeeded, result.Status);
        Assert.True(beforeFailure.Requests[^1].Input.Count > 120);
        var replay = Assert.Single(httpClient.Inputs);
        Assert.True(replay.Count <= 120);
        Assert.Empty(AdditionalToolItems(replay));
        var preservedCheckpoint = Assert.Single(replay.OfType<JsonObject>(), item => item["type"]?.GetValue<string>() == "compaction");
        Assert.True(JsonNode.DeepEquals(checkpoint, preservedCheckpoint));
        Assert.Equal("developer", replay[0]?["role"]?.GetValue<string>());
        Assert.True(replay.IndexOf(preservedCheckpoint) > 0);
        var callPairs = replay.OfType<JsonObject>()
            .Where(item => item["type"]?.GetValue<string>() is "function_call" or "function_call_output")
            .GroupBy(item => item["call_id"]!.GetValue<string>()).ToArray();
        Assert.DoesNotContain(callPairs, pair => pair.Key == "tap-0");
        Assert.Contains(callPairs, pair => pair.Key == $"tap-{actionCount - 1}");
        Assert.All(callPairs, pair =>
        {
            Assert.Single(pair, item => item["type"]?.GetValue<string>() == "function_call");
            Assert.Single(pair, item => item["type"]?.GetValue<string>() == "function_call_output");
        });
    }

    [Theory]
    [InlineData(SimulatorAgentOpenAiProtocol.Http, "gpt-5.6-luna", false)]
    [InlineData(SimulatorAgentOpenAiProtocol.WebSocket, "gpt-5.5", false)]
    [InlineData(SimulatorAgentOpenAiProtocol.WebSocket, "gpt-5.6-luna", true)]
    public async Task RunAsync_KeepsTheFullCatalogForLegacyHttpAndAppGraphRuns(
        SimulatorAgentOpenAiProtocol protocol, string model, bool appGraphEnabled)
    {
        var gateway = new FakeToolGateway();
        AddToolLoadingDefinitions(gateway, "ansight_find_ui", "ansight_swipe_ui");
        var session = new FakeOpenAiSession([CompleteWebSocketTurn()]);
        var httpClient = new FakeOpenAiClient([CompleteWebSocketTurn()]);
        using var service = CreateToolLoadingService(session, gateway, httpClient);

        var result = await service.RunAsync(ToolLoadingRequest("Inspect account.") with
        {
            Model = model, OpenAiProtocol = protocol, AppGraphEnabled = appGraphEnabled,
            AppGraphPlans = appGraphEnabled
                ? [new SimulatorAgentAppGraphPlan(
                    Guid.NewGuid(), Guid.NewGuid(), "Open account", "Open account.", "Account visible",
                    [new SimulatorAgentAppGraphTransition(
                        1, "open-account", "Home", "Account visible", "Tap Account", ["Account is visible."],
                        [new SimulatorAgentAppGraphBinding(
                            Guid.NewGuid(), 1, "ui_action",
                            new JsonObject { ["action"] = "tap", ["automationId"] = "account" },
                            [], ["Account is visible."], 1m)])])]
                : []
        });

        Assert.Equal(SimulatorAgentRunStatus.Succeeded, result.Status);
        var request = protocol == SimulatorAgentOpenAiProtocol.Http
            ? Assert.Single(httpClient.Requests) : Assert.Single(session.Requests);
        Assert.Contains("ansight_find_ui", ToolNames(request.Tools));
        Assert.Contains("ansight_swipe_ui", ToolNames(request.Tools));
        Assert.DoesNotContain("ansight_load_tools", ToolNames(request.Tools));
        Assert.Empty(AdditionalToolItems(request.Input));
        Assert.Contains(AgentToolCatalog.ReadGuidance("manual-ui"), request.Instructions, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_InstructionBoundaryResetsLoadedCapabilitiesWithTheConversation()
    {
        var gateway = new FakeToolGateway();
        AddToolLoadingDefinitions(gateway, "ansight_find_ui");
        var session = new FakeOpenAiSession([
            LoadManualToolsTurn("first-load"), CompleteWebSocketTurn(),
            CreateFunctionTurn("second-hidden", "ansight_find_ui", new JsonObject { ["automationId"] = "account" }),
            LoadManualToolsTurn("second-load"),
            CreateFunctionTurn("second-visible", "ansight_find_ui", new JsonObject { ["automationId"] = "account" }),
            CompleteWebSocketTurn()
        ]);
        using var service = CreateToolLoadingService(session, gateway);

        var result = await service.RunAsync(ToolLoadingRequest("Inspect account.") with
        {
            Instructions = ["Inspect account.", "Inspect account again."]
        });

        Assert.Equal(SimulatorAgentRunStatus.Succeeded, result.Status);
        Assert.True(session.Requests[2].StartNewConversation);
        Assert.Equal("instruction-boundary", session.Requests[2].ReplayReason);
        Assert.Empty(AdditionalToolItems(session.Requests[2].Input));
        Assert.True(Assert.Single(result.Audit.ToolCalls, call => call.CallId == "second-hidden").IsError);
        Assert.Single(AdditionalToolItems(session.Requests[4].IncrementalInput!));
        Assert.Single(gateway.Calls);
    }

    private static SimulatorAgentRunRequest ToolLoadingRequest(string instruction)
        => new("session-123", [instruction]) { OpenAiProtocol = SimulatorAgentOpenAiProtocol.WebSocket };

    private static OpenAiTurn LoadManualToolsTurn(string callId)
        => CreateFunctionTurn(callId, "ansight_load_tools", new JsonObject { ["bundle"] = "manual-ui" });

    private static IEnumerable<JsonObject> AdditionalToolItems(JsonArray input)
        => input.OfType<JsonObject>().Where(item => item["type"]?.GetValue<string>() == "additional_tools");

    private static string[] ToolNames(JsonArray definitions)
        => definitions.OfType<JsonObject>().Select(tool => tool["name"]!.GetValue<string>()).ToArray();

    private static void AddToolLoadingDefinitions(FakeToolGateway gateway, params string[] additionalNames)
    {
        foreach (var name in new[] { "ansight_get_live_visual_tree", "ansight_list_tasks", "ansight_run_task" }.Concat(additionalNames))
        {
            gateway.ToolDefinitions.Add(new JsonObject
            {
                ["type"] = "function", ["name"] = name,
                ["parameters"] = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() }
            });
        }
    }

    private static SimulatorAgentService CreateToolLoadingService(
        IOpenAiSession session, FakeToolGateway gateway, FakeOpenAiClient? httpClient = null)
    {
        var service = new SimulatorAgentService(new InMemoryEncryptedStorage(), httpClient ?? new FakeOpenAiClient([]),
            gateway, openAiWebSocketSessionFactory: new FakeOpenAiSessionFactory(session));
        service.SetDefaultModelAccessTokenForTesting("sk-local-test");
        return service;
    }

    private sealed class FailAfterToolLoadingSession(FakeOpenAiSession inner, int successfulRequests) : IOpenAiSession
    {
        public Task<OpenAiTurn> CreateResponseAsync(OpenAiRequest request, CancellationToken cancellationToken)
            => inner.Requests.Count >= successfulRequests
                ? throw new OpenAiWebSocketTransportException("Connection closed after loading tools.")
                : inner.CreateResponseAsync(request, cancellationToken);

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
