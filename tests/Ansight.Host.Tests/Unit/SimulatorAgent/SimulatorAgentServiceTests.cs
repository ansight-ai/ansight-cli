using System.Text.Json.Nodes;
using Ansight.Host.AppGraphs;
using Ansight.Infrastructure.Security;

namespace Ansight.Host.Tests.Unit.SimulatorAgent;

public sealed partial class SimulatorAgentServiceTests
{
    [Fact]
    public void RunRequest_DefaultBudgetSupportsLongAgentLoops()
    {
        var request = new SimulatorAgentRunRequest("session-123", ["Validate the app."]);

        Assert.Equal(64, request.MaximumTurnsPerInstruction);
        Assert.Equal(64, request.MaximumRoundTrips);
        Assert.Equal(512, request.MaximumToolCalls);
        Assert.Equal(SimulatorAgentService.MaximumInstructionCharacters, request.MaximumInstructionCharacters);
        Assert.Null(request.MaximumModelOutputTokens);
        Assert.Null(request.AppGraphExplorationName);
        Assert.True(request.ContinueAfterInstructionFailure);
        Assert.False(request.CaptureTrace);
        Assert.Equal(SimulatorAgentOpenAiProtocol.Http, request.OpenAiProtocol);
    }

    [Fact]
    public async Task RunAsync_UsesWebSocketSessionForLocalAutoExecution()
    {
        var storage = new InMemoryEncryptedStorage();
        var httpClient = new FakeOpenAiClient([]);
        var webSocketSession = new FakeOpenAiSession(
        [
            new OpenAiTurn(
                "resp-thinking",
                "gpt-5.6-terra",
                [],
                [],
                string.Empty,
                SimulatorAgentTokenUsage.Empty),
            CreateFunctionTurn(
                "call-complete",
                "complete_instruction",
                new JsonObject
                {
                    ["outcome"] = "succeeded",
                    ["summary"] = "Done."
                })
        ]);
        using var service = new SimulatorAgentService(
            storage,
            httpClient,
            new FakeToolGateway(),
            openAiWebSocketSessionFactory: new FakeOpenAiSessionFactory(webSocketSession));
        service.SetDefaultModelAccessTokenForTesting("sk-local-test");

        var result = await service.RunAsync(new SimulatorAgentRunRequest(
            "session-123",
            ["Validate the app."])
        {
            OpenAiProtocol = SimulatorAgentOpenAiProtocol.Auto
        });

        Assert.Equal(SimulatorAgentRunStatus.Succeeded, result.Status);
        Assert.Equal("websocket", result.Audit.OpenAiProtocol);
        Assert.Contains(result.Audit.StartupSteps, step => step.Name == "Resolve session context");
        Assert.Contains(result.Audit.StartupSteps, step => step.Name == "Build initial model context");
        Assert.All(result.Audit.StartupSteps, step => Assert.True(step.DurationMilliseconds >= 0));
        Assert.Empty(httpClient.Inputs);
        Assert.Equal(2, webSocketSession.Requests.Count);
        Assert.True(webSocketSession.Requests[0].StartNewConversation);
        Assert.False(webSocketSession.Requests[1].StartNewConversation);
        var incrementalInput = webSocketSession.IncrementalInputs[1];
        var feedback = Assert.Single(incrementalInput.OfType<JsonObject>());
        Assert.Equal("user", feedback["role"]?.GetValue<string>());
        Assert.True(webSocketSession.IsDisposed);
    }

    [Fact]
    public async Task RunAsync_AutoFallsBackToHttpAfterWebSocketTransportFailure()
    {
        var storage = new InMemoryEncryptedStorage();
        var httpClient = new FakeOpenAiClient(
        [
            CreateFunctionTurn(
                "call-complete",
                "complete_instruction",
                new JsonObject
                {
                    ["outcome"] = "succeeded",
                    ["summary"] = "Done."
                })
        ]);
        using var service = new SimulatorAgentService(
            storage,
            httpClient,
            new FakeToolGateway(),
            openAiWebSocketSessionFactory: new FakeOpenAiSessionFactory(
                new FailingOpenAiSession()));
        service.SetDefaultModelAccessTokenForTesting("sk-local-test");

        var result = await service.RunAsync(new SimulatorAgentRunRequest(
            "session-123",
            ["Validate the app."])
        {
            OpenAiProtocol = SimulatorAgentOpenAiProtocol.Auto
        });

        Assert.Equal(SimulatorAgentRunStatus.Succeeded, result.Status);
        Assert.Equal("http-fallback", result.Audit.OpenAiProtocol);
        Assert.Single(httpClient.Inputs);
    }

    [Fact]
    public async Task RunAsync_KeepsToolHistoryAppendOnlyAcrossWebSocketTurns()
    {
        var storage = new InMemoryEncryptedStorage();
        var webSocketSession = new FakeOpenAiSession(
        [
            CreateFunctionTurn(
                "call-find-first",
                "ansight_find_ui",
                new JsonObject { ["automationId"] = "first" }),
            CreateFunctionTurn(
                "call-find-second",
                "ansight_find_ui",
                new JsonObject { ["automationId"] = "second" }),
            CreateFunctionTurn(
                "call-complete",
                "complete_instruction",
                new JsonObject
                {
                    ["outcome"] = "succeeded",
                    ["summary"] = "Done."
                })
        ]);
        var gateway = new FakeToolGateway();
        gateway.Results.Enqueue(new ToolCallResult(
            false,
            "{\"isError\":false,\"result\":{\"matches\":[]}}",
            "First query completed."));
        gateway.Results.Enqueue(new ToolCallResult(
            false,
            "{\"isError\":false,\"result\":{\"matches\":[]}}",
            "Second query completed."));
        using var service = new SimulatorAgentService(
            storage,
            new FakeOpenAiClient([]),
            gateway,
            openAiWebSocketSessionFactory: new FakeOpenAiSessionFactory(webSocketSession));
        service.SetDefaultModelAccessTokenForTesting("sk-local-test");

        var result = await service.RunAsync(new SimulatorAgentRunRequest(
            "session-123",
            ["Inspect both controls."])
        {
            OpenAiProtocol = SimulatorAgentOpenAiProtocol.WebSocket
        });

        Assert.Equal(SimulatorAgentRunStatus.Succeeded, result.Status);
        Assert.Equal(3, webSocketSession.Requests.Count);
        Assert.True(webSocketSession.Requests[0].StartNewConversation);
        Assert.False(webSocketSession.Requests[1].StartNewConversation);
        Assert.False(webSocketSession.Requests[2].StartNewConversation);
        Assert.DoesNotContain("superseded", webSocketSession.Requests[2].Input.ToJsonString());
        Assert.Equal(2, webSocketSession.Requests[2].Input.OfType<JsonObject>()
            .Count(static item => item["type"]?.GetValue<string>() == "function_call_output"));
        var latest = Assert.Single(webSocketSession.IncrementalInputs[2].OfType<JsonObject>(),
            static item => item["type"]?.GetValue<string>() == "function_call_output");
        Assert.Equal("call-find-second", latest["call_id"]?.GetValue<string>());
        Assert.All(webSocketSession.Requests, static sent => Assert.Equal(32_000, sent.CompactThresholdTokens));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunAsync_AllowsScreenScanForDeviceAndSdkExecution(bool deviceMode)
    {
        var storage = new InMemoryEncryptedStorage();
        var client = new FakeOpenAiClient(
        [
            CreateFunctionTurn(
                "call-scan",
                "ansight_scan_screen",
                new JsonObject()),
            CreateFunctionTurn(
                "call-complete",
                "complete_instruction",
                new JsonObject
                {
                    ["outcome"] = "succeeded",
                    ["summary"] = "Done."
                })
        ]);
        var gateway = new FakeToolGateway
        {
            Capabilities = SessionCapabilities.Empty("ios") with { IsDeviceOnly = deviceMode }
        };
        foreach (var toolName in new[] { "ansight_scan_screen", "ansight_find_ui" })
        {
            gateway.ToolDefinitions.Add(new JsonObject
            {
                ["type"] = "function",
                ["name"] = toolName,
                ["parameters"] = new JsonObject { ["type"] = "object" }
            });
        }
        using var service = new SimulatorAgentService(storage, client, gateway);
        service.SetDefaultModelAccessTokenForTesting("sk-local-test");

        var result = await service.RunAsync(new SimulatorAgentRunRequest(
            "session-123",
            ["Inspect the app."]));

        Assert.Equal(SimulatorAgentRunStatus.Succeeded, result.Status);
        Assert.Contains(client.Tools[0].OfType<JsonObject>(),
            tool => tool["name"]?.GetValue<string>() == "ansight_scan_screen");
        Assert.Equal(deviceMode ? "device" : "sdk", result.Audit.ExecutionMode);
        Assert.Contains(
            client.Tools[0].OfType<JsonObject>(),
            tool => tool["name"]?.GetValue<string>() == "ansight_find_ui");
        Assert.Single(gateway.Calls);
        var scan = Assert.Single(
            result.Audit.ToolCalls,
            call => call.CallId == "call-scan");
        Assert.False(scan.IsError);
        Assert.True(RunRequestContext.AllowsScreenshotOcr(scan.CorrelationId));
    }

    [Fact]
    public async Task RunAsync_AcceptsAppGraphExplorationBudget()
    {
        var storage = new InMemoryEncryptedStorage();
        var client = new FakeOpenAiClient(
        [
            CreateFunctionTurn(
                "call-progress",
                "report_app_graph_progress",
                new JsonObject
                {
                    ["message"] = "Observed the only destination.",
                    ["currentDestinationId"] = "home",
                    ["destinations"] = new JsonArray(
                        new JsonObject
                        {
                            ["id"] = "home",
                            ["kind"] = "screen",
                            ["name"] = "Home",
                            ["parentScreen"] = string.Empty,
                            ["synonyms"] = new JsonArray(),
                            ["purpose"] = "Show the app.",
                            ["description"] = "Home is visible.",
                            ["scrollStatus"] = "not_scrollable",
                            ["confidence"] = 0.9
                        }),
                    ["transitions"] = new JsonArray(),
                    ["actions"] = new JsonArray(),
                    ["coverage"] = new JsonObject
                    {
                        ["safeActionsObserved"] = 0,
                        ["actionsExplored"] = 0,
                        ["scrollContainersObserved"] = 0,
                        ["scrollContainersCompleted"] = 0,
                        ["gaps"] = new JsonArray()
                    }
                }),
            CreateFunctionTurn(
                "call-complete",
                "complete_instruction",
                new JsonObject
                {
                    ["outcome"] = "succeeded",
                    ["summary"] = CreateAppGraphCompletionSummary("home")
                })
        ]);
        using var service = new SimulatorAgentService(storage, client, new FakeToolGateway());
        service.SetDefaultModelAccessTokenForTesting("sk-local-test");

        var result = await service.RunAsync(new SimulatorAgentRunRequest(
            "session-123",
            ["Explore the app."],
            MaximumTurnsPerInstruction: 1_023)
        {
            MaximumRoundTrips = 1_024,
            AppGraphExplorationName = "Default",
            CaptureTrace = true
        });

        Assert.Equal(SimulatorAgentRunStatus.Succeeded, result.Status);
        Assert.Equal(1_023, result.Audit.MaximumTurnsPerInstruction);
        Assert.Equal(1_024, result.Audit.MaximumRoundTrips);
        Assert.Contains("report_app_graph_progress", result.Audit.AgentPrompt, StringComparison.Ordinal);
        Assert.Contains("App Graph teaching", result.Audit.AgentPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("Authorized App Graph routes are available", result.Audit.AgentPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_AppGraphRejectsBroadFindUiEnumeration()
    {
        var storage = new InMemoryEncryptedStorage();
        var client = new FakeOpenAiClient(
        [
            CreateFunctionTurn(
                "call-find-all",
                "ansight_find_ui",
                new JsonObject
                {
                    ["visible"] = true,
                    ["limit"] = 100
                }),
            CreateFunctionTurn(
                "call-progress",
                "report_app_graph_progress",
                new JsonObject
                {
                    ["message"] = "Observed Home from the live visual tree.",
                    ["currentDestinationId"] = "home",
                    ["destinations"] = new JsonArray(
                        new JsonObject
                        {
                            ["id"] = "home",
                            ["kind"] = "screen",
                            ["name"] = "Home",
                            ["parentScreen"] = string.Empty,
                            ["synonyms"] = new JsonArray(),
                            ["purpose"] = "Navigate the app.",
                            ["description"] = "Home is visible.",
                            ["scrollStatus"] = "not_scrollable",
                            ["confidence"] = 0.9
                        }),
                    ["transitions"] = new JsonArray(),
                    ["actions"] = new JsonArray(),
                    ["coverage"] = new JsonObject
                    {
                        ["safeActionsObserved"] = 0,
                        ["actionsExplored"] = 0,
                        ["scrollContainersObserved"] = 0,
                        ["scrollContainersCompleted"] = 0,
                        ["gaps"] = new JsonArray()
                    }
                }),
            CreateFunctionTurn(
                "call-complete",
                "complete_instruction",
                new JsonObject
                {
                    ["outcome"] = "succeeded",
                    ["summary"] = CreateAppGraphCompletionSummary("home")
                })
        ]);
        var gateway = new FakeToolGateway();
        gateway.ToolDefinitions.Add(new JsonObject
        {
            ["type"] = "function",
            ["name"] = "ansight_find_ui",
            ["parameters"] = new JsonObject { ["type"] = "object" }
        });
        using var service = new SimulatorAgentService(storage, client, gateway);
        service.SetDefaultModelAccessTokenForTesting("sk-local-test");

        var result = await service.RunAsync(new SimulatorAgentRunRequest(
            "session-123",
            ["Explore the app."])
        {
            AppGraphExplorationName = "Default"
        });

        Assert.Equal(SimulatorAgentRunStatus.Succeeded, result.Status);
        Assert.Empty(gateway.Calls);
        var rejectedCall = Assert.Single(
            result.Audit.ToolCalls,
            call => call.CallId == "call-find-all");
        Assert.True(rejectedCall.IsError);
        Assert.Contains("does not allow broad find_ui enumeration", rejectedCall.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_AppGraphCapsScrollAttemptsPerScope()
    {
        var storage = new InMemoryEncryptedStorage();
        var scrollArguments = new JsonObject
        {
            ["orientation"] = "down",
            ["direction"] = "down",
            ["length"] = 0.75
        };
        var client = new FakeOpenAiClient(
        [
            CreateFunctionTurn("scroll-1", "ansight_scroll_ui", scrollArguments.DeepClone().AsObject()),
            CreateFunctionTurn("scroll-2", "ansight_scroll_ui", scrollArguments.DeepClone().AsObject()),
            CreateFunctionTurn("scroll-3", "ansight_scroll_ui", scrollArguments.DeepClone().AsObject()),
            CreateFunctionTurn(
                "call-progress",
                "report_app_graph_progress",
                new JsonObject
                {
                    ["message"] = "The scroll attempt limit blocked further coverage.",
                    ["currentDestinationId"] = "home",
                    ["destinations"] = new JsonArray(
                        new JsonObject
                        {
                            ["id"] = "home",
                            ["kind"] = "screen",
                            ["name"] = "Home",
                            ["parentScreen"] = string.Empty,
                            ["synonyms"] = new JsonArray(),
                            ["purpose"] = "Navigate the app.",
                            ["description"] = "Home is visible.",
                            ["scrollStatus"] = "blocked",
                            ["confidence"] = 0.8
                        }),
                    ["transitions"] = new JsonArray(),
                    ["actions"] = new JsonArray(),
                    ["coverage"] = new JsonObject
                    {
                        ["safeActionsObserved"] = 0,
                        ["actionsExplored"] = 0,
                        ["scrollContainersObserved"] = 1,
                        ["scrollContainersCompleted"] = 0,
                        ["gaps"] = new JsonArray("Scroll coverage stopped at the configured attempt limit.")
                    }
                }),
            CreateFunctionTurn(
                "call-complete",
                "complete_instruction",
                new JsonObject
                {
                    ["outcome"] = "succeeded",
                    ["summary"] = CreateAppGraphCompletionSummary("home")
                })
        ]);
        var gateway = new FakeToolGateway();
        gateway.ToolDefinitions.Add(new JsonObject
        {
            ["type"] = "function",
            ["name"] = "ansight_scroll_ui",
            ["parameters"] = new JsonObject { ["type"] = "object" }
        });
        using var service = new SimulatorAgentService(storage, client, gateway);
        service.SetDefaultModelAccessTokenForTesting("sk-local-test");

        var result = await service.RunAsync(new SimulatorAgentRunRequest(
            "session-123",
            ["Explore the app."])
        {
            AppGraphExplorationName = "Default"
        });

        Assert.Equal(SimulatorAgentRunStatus.Succeeded, result.Status);
        Assert.Equal(2, gateway.Calls.Count(call => call.ToolName == "ansight_scroll_ui"));
        var guardedCall = Assert.Single(
            result.Audit.ToolCalls,
            call => call.CallId == "scroll-3");
        Assert.True(guardedCall.IsError);
        Assert.Contains("2-attempt limit", guardedCall.Message, StringComparison.Ordinal);
        var run = Assert.Single(service.ListAppGraphLiveRuns("session-123"));
        Assert.Equal("blocked", Assert.Single(run.Nodes).ScrollStatus);
    }

    [Fact]
    public async Task RunAsync_AppGraphCapsRepeatedStableActionsPerDestination()
    {
        var storage = new InMemoryEncryptedStorage();
        var tapArguments = new JsonObject
        {
            ["automationId"] = "open-known-branch-button"
        };

        OpenAiTurn CreateProgressTurn(
            string callId,
            string destinationId,
            bool includeDestination,
            string actionStatus)
            => CreateFunctionTurn(
                callId,
                "report_app_graph_progress",
                new JsonObject
                {
                    ["message"] = $"{destinationId} is current.",
                    ["currentDestinationId"] = destinationId,
                    ["destinations"] = includeDestination
                        ? new JsonArray(
                            new JsonObject
                            {
                                ["id"] = destinationId,
                                ["kind"] = "screen",
                                ["name"] = destinationId,
                                ["parentScreen"] = string.Empty,
                                ["synonyms"] = new JsonArray(),
                                ["purpose"] = "Exercise stable action guarding.",
                                ["description"] = $"{destinationId} is visible.",
                                ["scrollStatus"] = "not_scrollable",
                                ["confidence"] = 0.9
                            })
                        : new JsonArray(),
                    ["transitions"] = new JsonArray(),
                    ["actions"] = new JsonArray(
                        new JsonObject
                        {
                            ["id"] = $"action-{destinationId}-known-branch",
                            ["destinationId"] = destinationId,
                            ["toolName"] = "ansight_tap_ui",
                            ["automationId"] = "open-known-branch-button",
                            ["semanticMeaning"] = "Open known branch",
                            ["status"] = actionStatus,
                            ["lastOutcome"] = actionStatus == "explored"
                                ? "Destination change verified."
                                : string.Empty,
                            ["resultDestinationId"] = actionStatus == "explored"
                                ? destinationId
                                : string.Empty
                        }),
                    ["coverage"] = new JsonObject
                    {
                        ["safeActionsObserved"] = 1,
                        ["actionsExplored"] = 1,
                        ["scrollContainersObserved"] = 0,
                        ["scrollContainersCompleted"] = 0,
                        ["gaps"] = new JsonArray()
                    }
                });

        var client = new FakeOpenAiClient(
        [
            CreateProgressTurn("progress-home-1", "home", includeDestination: true, "queued"),
            CreateFunctionTurn("tap-home-1", "ansight_tap_ui", tapArguments.DeepClone().AsObject()),
            CreateProgressTurn("progress-home-2", "home", includeDestination: false, "explored"),
            CreateFunctionTurn("tap-home-2", "ansight_tap_ui", tapArguments.DeepClone().AsObject()),
            CreateProgressTurn("progress-profile", "profile", includeDestination: true, "queued"),
            CreateFunctionTurn("tap-profile-1", "ansight_tap_ui", tapArguments.DeepClone().AsObject()),
            CreateProgressTurn("progress-final", "profile", includeDestination: false, "explored"),
            CreateFunctionTurn(
                "call-complete",
                "complete_instruction",
                new JsonObject
                {
                    ["outcome"] = "succeeded",
                    ["summary"] = CreateAppGraphCompletionSummary("home", "profile")
                })
        ]);
        var gateway = new FakeToolGateway();
        gateway.ToolDefinitions.Add(new JsonObject
        {
            ["type"] = "function",
            ["name"] = "ansight_tap_ui",
            ["parameters"] = new JsonObject { ["type"] = "object" }
        });
        using var service = new SimulatorAgentService(storage, client, gateway);
        service.SetDefaultModelAccessTokenForTesting("sk-local-test");

        var result = await service.RunAsync(new SimulatorAgentRunRequest(
            "session-123",
            ["Explore the app."])
        {
            AppGraphExplorationName = "Default"
        });

        Assert.Equal(SimulatorAgentRunStatus.Succeeded, result.Status);
        Assert.Equal(2, gateway.Calls.Count(call => call.ToolName == "ansight_tap_ui"));
        var guardedCall = Assert.Single(
            result.Audit.ToolCalls,
            call => call.CallId == "tap-home-2");
        Assert.True(guardedCall.IsError);
        Assert.Contains("already terminal", guardedCall.Message, StringComparison.Ordinal);
        Assert.Contains("No queued action remains", guardedCall.Message, StringComparison.Ordinal);
        Assert.False(Assert.Single(
            result.Audit.ToolCalls,
            call => call.CallId == "tap-profile-1").IsError);
    }

    [Fact]
    public async Task RunAsync_PublishesAndRetainsLiveAppGraphProgress()
    {
        var storage = new InMemoryEncryptedStorage();
        var completionSummary = new JsonObject
        {
            ["schema"] = "ansight.app-graph-exploration/v1",
            ["summary"] = "Observed Map and Account.",
            ["confidence"] = 0.9,
            ["destinations"] = new JsonArray(
                new JsonObject
                {
                    ["id"] = "map",
                    ["kind"] = "screen",
                    ["name"] = "Map",
                    ["parentScreen"] = null,
                    ["synonyms"] = new JsonArray(),
                    ["purpose"] = "Browse areas.",
                    ["description"] = "MapPage visible."
                },
                new JsonObject
                {
                    ["id"] = "account",
                    ["kind"] = "screen",
                    ["name"] = "Account",
                    ["parentScreen"] = null,
                    ["synonyms"] = new JsonArray(),
                    ["purpose"] = "Manage account.",
                    ["description"] = "AccountPage visible."
                }),
            ["transitions"] = new JsonArray(
                new JsonObject
                {
                    ["id"] = "open-account",
                    ["from"] = "map",
                    ["to"] = "account",
                    ["action"] = new JsonObject
                    {
                        ["automationId"] = "open-account-button",
                        ["semanticMeaning"] = "Open Account"
                    },
                    ["binding"] = new JsonObject { ["confidence"] = 0.95 }
                })
        }.ToJsonString();
        var client = new FakeOpenAiClient(
        [
            CreateFunctionTurn(
                "call-progress",
                "report_app_graph_progress",
                new JsonObject
                {
                    ["message"] = "Account is now visible.",
                    ["currentDestinationId"] = "account",
                    ["destinations"] = new JsonArray(
                        new JsonObject
                        {
                            ["id"] = "map",
                            ["kind"] = "screen",
                            ["name"] = "Map",
                            ["parentScreen"] = string.Empty,
                            ["synonyms"] = new JsonArray(),
                            ["purpose"] = "Browse areas.",
                            ["description"] = "MapPage visible.",
                            ["scrollStatus"] = "not_scrollable",
                            ["confidence"] = 0.98
                        },
                        new JsonObject
                        {
                            ["id"] = "account",
                            ["kind"] = "screen",
                            ["name"] = "Account",
                            ["parentScreen"] = string.Empty,
                            ["synonyms"] = new JsonArray(),
                            ["purpose"] = "Manage account.",
                            ["description"] = "AccountPage visible.",
                            ["scrollStatus"] = "complete",
                            ["confidence"] = 0.96
                        }),
                    ["transitions"] = new JsonArray(
                        new JsonObject
                        {
                            ["id"] = "open-account",
                            ["from"] = "map",
                            ["to"] = "account",
                            ["automationId"] = "open-account-button",
                            ["semanticMeaning"] = "Open Account",
                            ["confidence"] = 0.95
                        }),
                    ["actions"] = new JsonArray(),
                    ["coverage"] = new JsonObject
                    {
                        ["safeActionsObserved"] = 5,
                        ["actionsExplored"] = 2,
                        ["scrollContainersObserved"] = 1,
                        ["scrollContainersCompleted"] = 1,
                        ["gaps"] = new JsonArray("Settings has no stable automation ID.")
                    }
                }),
            CreateFunctionTurn(
                "call-complete",
                "complete_instruction",
                new JsonObject
                {
                    ["outcome"] = "succeeded",
                    ["summary"] = completionSummary
                }),
            CreateFunctionTurn(
                "call-progress-final",
                "report_app_graph_progress",
                new JsonObject
                {
                    ["message"] = "All safe actions and scroll containers are covered.",
                    ["currentDestinationId"] = "account",
                    ["destinations"] = new JsonArray(),
                    ["transitions"] = new JsonArray(),
                    ["actions"] = new JsonArray(),
                    ["coverage"] = new JsonObject
                    {
                        ["safeActionsObserved"] = 5,
                        ["actionsExplored"] = 5,
                        ["scrollContainersObserved"] = 1,
                        ["scrollContainersCompleted"] = 1,
                        ["gaps"] = new JsonArray()
                    }
                }),
            CreateFunctionTurn(
                "call-complete-final",
                "complete_instruction",
                new JsonObject
                {
                    ["outcome"] = "succeeded",
                    ["summary"] = completionSummary
                })
        ]);
        var gateway = new FakeToolGateway();
        foreach (var toolName in new[]
                 {
                     "ansight_tap_ui",
                     "ansight_scan_screen",
                     "ansight_list_tasks",
                     "ansight_describe_module",
                     "ansight_run_task",
                     "ansight_list_app_tools",
                     "ansight_call_app_tool"
                 })
        {
            gateway.ToolDefinitions.Add(new JsonObject
            {
                ["type"] = "function",
                ["name"] = toolName,
                ["parameters"] = new JsonObject { ["type"] = "object" }
            });
        }
        using var service = new SimulatorAgentService(storage, client, gateway);
        service.SetDefaultModelAccessTokenForTesting("sk-local-test");

        var result = await service.RunAsync(new SimulatorAgentRunRequest(
            "session-123",
            ["Explore every screen."],
            AppId: "com.example.app")
        {
            AppGraphExplorationName = "Example Graph"
        });

        Assert.Equal(SimulatorAgentRunStatus.Succeeded, result.Status);
        var run = Assert.Single(service.ListAppGraphLiveRuns("session-123"));
        Assert.Equal(result.Audit.RunId, run.RunId);
        Assert.Equal("succeeded", run.Status);
        Assert.Equal("Example Graph", run.GraphName);
        Assert.Equal(2, run.Nodes.Count);
        Assert.Single(run.Edges);
        Assert.Equal(5, run.Coverage.SafeActionsObserved);
        Assert.Equal(5, run.Coverage.ActionsExplored);
        Assert.Contains("Settings has no stable automation ID.", run.Coverage.Gaps);
        Assert.Contains(
            client.Tools[0].OfType<JsonObject>(),
            tool => tool["name"]?.GetValue<string>() == "report_app_graph_progress");
        var reportTool = Assert.Single(
            client.Tools[0].OfType<JsonObject>(),
            tool => tool["name"]?.GetValue<string>() == "report_app_graph_progress");
        var reportProperties = Assert.IsType<JsonObject>(reportTool["parameters"]?["properties"]);
        Assert.True(reportProperties.ContainsKey("navigationHosts"));
        Assert.True(reportProperties.ContainsKey("tabGroups"));
        var navigationHostItemProperties = Assert.IsType<JsonObject>(
            reportProperties["navigationHosts"]?["items"]?["properties"]);
        var tabGroupItemProperties = Assert.IsType<JsonObject>(
            reportProperties["tabGroups"]?["items"]?["properties"]);
        Assert.True(navigationHostItemProperties.ContainsKey("technology"));
        Assert.True(tabGroupItemProperties.ContainsKey("technology"));
        Assert.Contains(
            client.Tools[0].OfType<JsonObject>(),
            tool => tool["name"]?.GetValue<string>() == "ansight_tap_ui");
        Assert.DoesNotContain(
            client.Tools[0].OfType<JsonObject>(),
            tool => tool["name"]?.GetValue<string>() is "ansight_scan_screen"
                or "ansight_list_tasks"
                or "ansight_describe_module"
                or "ansight_run_task"
                or "ansight_list_app_tools"
                or "ansight_call_app_tool");
        Assert.Contains(
            result.Audit.ToolCalls,
            call => call.ToolName == "report_app_graph_progress" && !call.IsAnsightTool && !call.IsError);
        Assert.NotEmpty(run.Trace);
        Assert.Equal("Starting", run.Trace[0].Stage);
        Assert.Contains(run.Trace, entry => entry.Stage == "AppGraphUpdated");
        Assert.Contains(
            run.Trace,
            entry => entry.Stage == "ToolCompleted"
                     && entry.Message.Contains("rejected", StringComparison.Ordinal));
        Assert.Contains(run.Trace, entry => entry.Stage == "InstructionCompleted");
        Assert.Equal("Completed", run.Trace[^1].Stage);
        Assert.True(run.Trace.Select(entry => entry.Sequence).SequenceEqual(
            run.Trace.Select(entry => entry.Sequence).Order()));
    }

    [Fact]
    public async Task RunAsync_RejectsFailedAppGraphCompletionWhileReportedCoverageIsIncomplete()
    {
        var storage = new InMemoryEncryptedStorage();
        static JsonObject CreateAppGraphAction(string id, string status)
            => new()
            {
                ["id"] = id,
                ["destinationId"] = "home",
                ["toolName"] = "ansight_tap_ui",
                ["automationId"] = id,
                ["semanticMeaning"] = $"Explore {id}",
                ["status"] = status,
                ["lastOutcome"] = status is "explored" or "blocked"
                    ? $"{id} reached terminal status."
                    : string.Empty,
                ["resultDestinationId"] = status == "explored" ? "home" : string.Empty
            };
        var client = new FakeOpenAiClient(
        [
            CreateFunctionTurn(
                "call-progress",
                "report_app_graph_progress",
                new JsonObject
                {
                    ["message"] = "The Home branch remains incomplete.",
                    ["currentDestinationId"] = "home",
                    ["destinations"] = new JsonArray(
                        new JsonObject
                        {
                            ["id"] = "home",
                            ["kind"] = "screen",
                            ["name"] = "Home",
                            ["parentScreen"] = string.Empty,
                            ["synonyms"] = new JsonArray(),
                            ["purpose"] = "Navigate the app.",
                            ["description"] = "Home is visible.",
                            ["scrollStatus"] = "in_progress",
                            ["confidence"] = 0.95
                        }),
                    ["transitions"] = new JsonArray(),
                    ["actions"] = new JsonArray(
                        CreateAppGraphAction("home-action-1", "explored"),
                        CreateAppGraphAction("home-action-2", "queued"),
                        CreateAppGraphAction("home-action-3", "queued")),
                    ["coverage"] = new JsonObject
                    {
                        ["safeActionsObserved"] = 3,
                        ["actionsExplored"] = 1,
                        ["scrollContainersObserved"] = 1,
                        ["scrollContainersCompleted"] = 0,
                        ["gaps"] = new JsonArray("Two branches and one scroll container remain.")
                    }
                }),
            CreateFunctionTurn(
                "call-premature-failure",
                "complete_instruction",
                new JsonObject
                {
                    ["outcome"] = "failed",
                    ["summary"] = "Exploration could not be completed because branches remain."
                }),
            CreateFunctionTurn(
                "call-progress-final",
                "report_app_graph_progress",
                new JsonObject
                {
                    ["message"] = "All reported branches are covered.",
                    ["currentDestinationId"] = "home",
                    ["destinations"] = new JsonArray(
                        new JsonObject
                        {
                            ["id"] = "home",
                            ["kind"] = "screen",
                            ["name"] = "Home",
                            ["parentScreen"] = string.Empty,
                            ["synonyms"] = new JsonArray(),
                            ["purpose"] = "Navigate the app.",
                            ["description"] = "Home coverage is complete.",
                            ["scrollStatus"] = "complete",
                            ["confidence"] = 0.95
                        }),
                    ["transitions"] = new JsonArray(),
                    ["actions"] = new JsonArray(
                        CreateAppGraphAction("home-action-2", "explored"),
                        CreateAppGraphAction("home-action-3", "blocked")),
                    ["coverage"] = new JsonObject
                    {
                        ["safeActionsObserved"] = 3,
                        ["actionsExplored"] = 3,
                        ["scrollContainersObserved"] = 1,
                        ["scrollContainersCompleted"] = 1,
                        ["gaps"] = new JsonArray()
                    }
                }),
            CreateFunctionTurn(
                "call-complete",
                "complete_instruction",
                new JsonObject
                {
                    ["outcome"] = "succeeded",
                    ["summary"] = CreateAppGraphCompletionSummary("home")
                })
        ]);
        using var service = new SimulatorAgentService(storage, client, new FakeToolGateway());
        service.SetDefaultModelAccessTokenForTesting("sk-local-test");

        var result = await service.RunAsync(new SimulatorAgentRunRequest(
            "session-123",
            ["Explore every safe branch."])
        {
            AppGraphExplorationName = "Default"
        });

        Assert.Equal(SimulatorAgentRunStatus.Succeeded, result.Status);
        Assert.Equal(4, result.TotalTurns);
        var prematureCompletion = Assert.Single(
            result.Audit.ToolCalls,
            call => call.CallId == "call-premature-failure");
        Assert.True(prematureCompletion.IsError);
        Assert.Contains("host-tracked action", prematureCompletion.Message, StringComparison.Ordinal);
        Assert.Contains("home-action-2", prematureCompletion.Message, StringComparison.Ordinal);
        Assert.Contains("remain incomplete", prematureCompletion.Message, StringComparison.Ordinal);
        Assert.Contains(
            "host-owned action, scroll, or navigation-structure frontier remains incomplete",
            client.Inputs[2].ToJsonString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_RejectsInvalidAppGraphSummaryAndAcceptsRepairedDialogContract()
    {
        var storage = new InMemoryEncryptedStorage();
        var invalidSummary = new JsonObject
        {
            ["schema"] = AppGraphExplorationSummaryValidator.ExplorationSchema,
            ["summary"] = "Observed Home and Pending Edits.",
            ["destinations"] = new JsonArray(
                new JsonObject
                {
                    ["id"] = "home",
                    ["kind"] = "screen",
                    ["name"] = "Home",
                    ["parentScreen"] = null,
                    ["synonyms"] = new JsonArray(),
                    ["purpose"] = "Use the app."
                },
                new JsonObject
                {
                    ["id"] = "screen_pending_area_edits",
                    ["kind"] = "screen",
                    ["name"] = "Pending Edits & Uploads",
                    ["parentScreen"] = "screen_account",
                    ["synonyms"] = new JsonArray(),
                    ["purpose"] = "Review pending edits."
                }),
            ["navigationHosts"] = new JsonArray(),
            ["tabGroups"] = new JsonArray(),
            ["transitions"] = new JsonArray()
        };
        var repairedSummary = invalidSummary.DeepClone().AsObject();
        var repairedDialog = Assert.IsType<JsonObject>(repairedSummary["destinations"]?[1]);
        repairedDialog["kind"] = "dialog";
        repairedDialog["parentScreen"] = null;
        var client = new FakeOpenAiClient(
        [
            CreateFunctionTurn(
                "call-progress",
                "report_app_graph_progress",
                new JsonObject
                {
                    ["message"] = "All safe UI is covered.",
                    ["currentDestinationId"] = "home",
                    ["destinations"] = new JsonArray(
                        new JsonObject
                        {
                            ["id"] = "home",
                            ["kind"] = "screen",
                            ["name"] = "Home",
                            ["parentScreen"] = string.Empty,
                            ["synonyms"] = new JsonArray(),
                            ["purpose"] = "Use the app.",
                            ["description"] = "Home is visible.",
                            ["scrollStatus"] = "not_scrollable",
                            ["confidence"] = 0.95
                        }),
                    ["transitions"] = new JsonArray(),
                    ["actions"] = new JsonArray(),
                    ["coverage"] = new JsonObject
                    {
                        ["safeActionsObserved"] = 0,
                        ["actionsExplored"] = 0,
                        ["scrollContainersObserved"] = 0,
                        ["scrollContainersCompleted"] = 0,
                        ["gaps"] = new JsonArray()
                    }
                }),
            CreateFunctionTurn(
                "call-invalid-completion",
                "complete_instruction",
                new JsonObject
                {
                    ["outcome"] = "succeeded",
                    ["summary"] = invalidSummary.ToJsonString()
                }),
            CreateFunctionTurn(
                "call-repaired-completion",
                "complete_instruction",
                new JsonObject
                {
                    ["outcome"] = "succeeded",
                    ["summary"] = repairedSummary.ToJsonString()
                })
        ]);
        using var service = new SimulatorAgentService(storage, client, new FakeToolGateway());
        service.SetDefaultModelAccessTokenForTesting("sk-local-test");

        var result = await service.RunAsync(new SimulatorAgentRunRequest(
            "session-123",
            ["Explore every screen and dialog."],
            MaximumTurnsPerInstruction: 2)
        {
            AppGraphExplorationName = "Default",
            MaximumRoundTrips = 3
        });

        Assert.Equal(SimulatorAgentRunStatus.Succeeded, result.Status);
        Assert.Equal(3, result.TotalTurns);
        Assert.Equal(1, result.Audit.CompletionGracePassCount);
        var invalidCompletion = Assert.Single(
            result.Audit.ToolCalls,
            call => call.CallId == "call-invalid-completion");
        Assert.True(invalidCompletion.IsError);
        Assert.Contains(
            "screen_pending_area_edits",
            invalidCompletion.Message,
            StringComparison.Ordinal);
        Assert.Contains(
            "declared as a child of that navigation host",
            invalidCompletion.Message,
            StringComparison.Ordinal);
        Assert.False(Assert.Single(
            result.Audit.ToolCalls,
            call => call.CallId == "call-repaired-completion").IsError);
        Assert.Contains(
            "repair the named destination",
            client.Inputs[2].ToJsonString(),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunAsync_AcceptsLargerInstructionLimitForGraphExploration()
    {
        var storage = new InMemoryEncryptedStorage();
        var client = new FakeOpenAiClient(
        [
            CreateFunctionTurn(
                "call-complete",
                "complete_instruction",
                new JsonObject
                {
                    ["outcome"] = "succeeded",
                    ["summary"] = "The exploration completed."
                })
        ]);
        using var service = new SimulatorAgentService(storage, client, new FakeToolGateway());
        service.SetDefaultModelAccessTokenForTesting("sk-local-test");
        var instruction = new string('x', SimulatorAgentService.MaximumInstructionCharacters + 1);

        var result = await service.RunAsync(new SimulatorAgentRunRequest(
            "session-123",
            [instruction])
        {
            MaximumInstructionCharacters = 8_000,
            MaximumModelOutputTokens = 32_000
        });

        Assert.Equal(SimulatorAgentRunStatus.Succeeded, result.Status);
        Assert.Equal(32_000, result.Audit.MaximumModelOutputTokens);
        Assert.Equal(32_000, Assert.Single(client.MaximumOutputTokens));
    }

    [Fact]
    public async Task RunAsync_DefaultAuditKeepsMetadataButOmitsHeavyTracePayloads()
    {
        var storage = new InMemoryEncryptedStorage();
        var client = new FakeOpenAiClient(
        [
            CreateFunctionTurn(
                "call-complete",
                "complete_instruction",
                new JsonObject
                {
                    ["outcome"] = "succeeded",
                    ["summary"] = "The check passed."
                })
        ]);
        using var service = new SimulatorAgentService(storage, client, new FakeToolGateway());
        service.SetDefaultModelAccessTokenForTesting("sk-local-test");

        var result = await service.RunAsync(new SimulatorAgentRunRequest(
            "session-123",
            ["Verify the current screen."]));

        Assert.False(result.Audit.TraceEnabled);
        Assert.Null(result.Audit.AgentPrompt);
        Assert.Empty(result.Audit.AppGraphPlans);
        var modelPass = Assert.Single(result.Audit.ModelPasses);
        Assert.Null(modelPass.Context);
        Assert.Null(modelPass.AssistantText);
        var toolCall = Assert.Single(result.Audit.ToolCalls);
        Assert.Empty(toolCall.Arguments.Content);
        Assert.Empty(toolCall.Result.Content);
        Assert.True(toolCall.Arguments.OriginalCharacterCount > 0);
        Assert.True(toolCall.Result.OriginalCharacterCount > 0);
        Assert.NotEmpty(toolCall.Arguments.Sha256);
        Assert.NotEmpty(toolCall.Result.Sha256);
    }

    [Fact]
    public async Task RunAsync_EnforcesTotalRoundTripLimitAcrossInstructions()
    {
        var storage = new InMemoryEncryptedStorage();
        var client = new FakeOpenAiClient(
        [
            CreateFunctionTurn(
                "call-first-complete",
                "complete_instruction",
                new JsonObject
                {
                    ["outcome"] = "succeeded",
                    ["summary"] = "The first check passed."
                }),
            CreateFunctionTurn(
                "call-second-complete",
                "complete_instruction",
                new JsonObject
                {
                    ["outcome"] = "succeeded",
                    ["summary"] = "The second check passed."
                })
        ]);
        using var service = new SimulatorAgentService(storage, client, new FakeToolGateway());
        service.SetDefaultModelAccessTokenForTesting("sk-local-test");

        var result = await service.RunAsync(new SimulatorAgentRunRequest(
            "session-123",
            ["Check the first state.", "Check the second state."])
        {
            MaximumRoundTrips = 1
        });

        Assert.Equal(SimulatorAgentRunStatus.Failed, result.Status);
        Assert.Equal(1, result.TotalTurns);
        Assert.Equal(1, result.Audit.MaximumRoundTrips);
        Assert.Single(client.Inputs);
        Assert.Contains("round-trip organisation limit", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Constructor_RemovesLegacyLocalModelCredential()
    {
        var storage = new InMemoryEncryptedStorage();
        storage.Set("simulator-agent.openai-api-key.v1", "legacy-secret");

        using var service = new SimulatorAgentService(
            storage,
            new FakeOpenAiClient([]),
            new FakeToolGateway());

        Assert.Null(storage.Get("simulator-agent.openai-api-key.v1"));
    }

    [Fact]
    public async Task RunAsync_AuditsSecretAliasAndReplayOriginWithoutSecretValue()
    {
        var storage = new InMemoryEncryptedStorage();
        var client = new FakeOpenAiClient(
        [
            CreateFunctionTurn(
                "call-secret",
                "ansight_type_secret",
                new JsonObject
                {
                    ["secretAlias"] = "login.password",
                    ["automationId"] = "password-field"
                }),
            CreateFunctionTurn(
                "call-complete",
                "complete_instruction",
                new JsonObject
                {
                    ["outcome"] = "succeeded",
                    ["summary"] = "Signed in."
                })
        ]);
        using var service = new SimulatorAgentService(storage, client, new FakeToolGateway());
        service.SetDefaultModelAccessTokenForTesting("sk-local-test");
        var secret = service.SetTestSecret("com.example.app", "login.password", "sensitive-value");

        var result = await service.RunAsync(new SimulatorAgentRunRequest(
            "session-123",
            ["Sign in using the declared password secret."],
            AppId: "com.example.app")
        {
            SecretAliases = ["login.password"],
            ReplayedFromRunId = "original-run"
        });

        var reference = Assert.Single(result.Audit.SecretReferences);
        Assert.Equal("login.password", reference.Alias);
        Assert.Equal(secret.VersionId, reference.VersionId);
        Assert.Equal("original-run", result.Audit.ReplayedFromRunId);
        Assert.DoesNotContain("sensitive-value", result.Audit.ToolCalls[0].Arguments.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("sensitive-value", result.Audit.ToolCalls[0].Result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_ExecutesAnsightToolAndCompletesInstruction()
    {
        var storage = new InMemoryEncryptedStorage();
        var client = new FakeOpenAiClient(
        [
            CreateFunctionTurn(
                "call-observe",
                "ansight_get_live_visual_tree",
                new JsonObject
                {
                    ["maxNodes"] = 100
                }),
            CreateFunctionTurn(
                "call-complete",
                "complete_instruction",
                new JsonObject
                {
                    ["outcome"] = "succeeded",
                    ["summary"] = "The welcome screen is visible."
                })
        ]);
        var gateway = new FakeToolGateway();
        var auditStore = new FakeAuditStore();
        using var service = new SimulatorAgentService(storage, client, gateway, auditStore);
        service.SetDefaultModelAccessTokenForTesting("sk-local-test");

        var result = await service.RunAsync(new SimulatorAgentRunRequest(
            "session-123",
            ["Open the app and verify the welcome screen."])
        {
            CaptureTrace = true,
            MaximumInstructionCharacters = 8_000,
            MaximumModelOutputTokens = 32_000
        });

        Assert.Equal(SimulatorAgentRunStatus.Succeeded, result.Status);
        Assert.Single(result.Instructions);
        Assert.Equal(SimulatorAgentInstructionStatus.Succeeded, result.Instructions[0].Status);
        Assert.Equal(2, result.TotalTurns);
        Assert.Equal(1, result.TotalToolCalls);
        var call = Assert.Single(gateway.Calls);
        Assert.Equal("ansight_get_live_visual_tree", call.ToolName);
        Assert.Equal("session-123", call.SessionId);
        Assert.True(RunRequestContext.AllowsScreenshotOcr(call.CorrelationId));
        Assert.Equal(100, call.Arguments["maxNodes"]?.GetValue<int>());
        Assert.All(client.ApiKeys, key => Assert.Equal("sk-local-test", key));
        Assert.Contains("Already launched, connected, and foreground.", client.Inputs[0].ToJsonString());
        Assert.Contains("Example App (com.example.app)", client.Inputs[0].ToJsonString());
        Assert.Equal(30, result.Audit.Tokens.TotalTokens);
        Assert.Equal(16, result.Audit.SchemaVersion);
        Assert.True(result.Audit.TraceEnabled);
        Assert.Equal(20, result.Audit.Tokens.InputTokens);
        Assert.Equal(10, result.Audit.Tokens.OutputTokens);
        Assert.Equal(2, result.Audit.ModelPassCount);
        Assert.Equal(2, result.Audit.FunctionCallCount);
        Assert.Equal(1, result.Audit.AnsightToolCallCount);
        Assert.Equal("com.example.app", result.Audit.AppId);
        Assert.Equal("medium", result.Audit.ReasoningEffort);
        Assert.Contains("bounded mobile UI control agent", result.Audit.AgentPrompt, StringComparison.Ordinal);
        Assert.Contains("prerequisiteToolIds", result.Audit.AgentPrompt, StringComparison.Ordinal);
        Assert.Contains("virtualized or custom-rendered", result.Audit.AgentPrompt, StringComparison.Ordinal);
        Assert.Contains("Add role, type, action, or enabled constraints only when observed or required", result.Audit.AgentPrompt, StringComparison.Ordinal);
        Assert.Contains("maui.get_visual_tree", result.Audit.AgentPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("Authorized App Graph", result.Audit.AgentPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("report_app_graph_progress", result.Audit.AgentPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("toolId=ui.get_visual_tree", result.Audit.AgentPrompt, StringComparison.Ordinal);
        Assert.Contains("Search input echo is not a result", result.Audit.AgentPrompt, StringComparison.Ordinal);
        Assert.Contains("Selector fields are ANDed on one node", result.Audit.AgentPrompt, StringComparison.Ordinal);
        Assert.Contains("type belongs to the target itself", result.Audit.AgentPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("redpoint", result.Audit.AgentPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("CardSelectorView", result.Audit.AgentPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("map-dismiss-search-button", result.Audit.AgentPrompt, StringComparison.Ordinal);
        Assert.Equal("ansight-simulator-agent-v35", result.Audit.PromptCacheKey);
        Assert.Equal(32_000, result.Audit.MaximumModelOutputTokens);
        Assert.All(client.MaximumOutputTokens, value => Assert.Equal(32_000, value));
        Assert.Equal(64, result.Audit.MaximumRoundTrips);
        Assert.Equal("Example App", result.Audit.Environment?.AppName);
        Assert.True(result.Audit.Environment?.SessionWasLive);
        Assert.Equal("iPhone 16 Pro", result.Audit.Environment?.Device?.Model);
        Assert.Equal("iOS", result.Audit.Environment?.Device?.OperatingSystemName);
        Assert.Equal("test-device-123", result.Audit.Environment?.Device?.Identifier);
        Assert.Equal("/tmp/simulator-agent-audit.json", result.AuditFilePath);
        Assert.Same(result.Audit, auditStore.SavedAudit);
        Assert.Equal("ansight_get_live_visual_tree", result.Audit.ToolCalls[0].ToolName);
        Assert.Contains("isError", result.Audit.ToolCalls[0].Result.Content, StringComparison.Ordinal);
        Assert.Equal("complete_instruction", result.Audit.ToolCalls[1].ToolName);
        Assert.All(result.Audit.ModelPasses, pass => Assert.NotNull(pass.Context));
        var firstPassContext = JsonNode.Parse(result.Audit.ModelPasses[0].Context!.Content)!.AsObject();
        Assert.Contains("bounded mobile UI control agent", firstPassContext["instructions"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.True(JsonNode.DeepEquals(client.Inputs[0], firstPassContext["input"]));
        Assert.True(JsonNode.DeepEquals(client.Tools[0], firstPassContext["tools"]));
    }

    [Fact]
    public async Task RunAsync_IncludesExplicitlySuppliedPublishedAppGraphRouteInAgentInputAndAudit()
    {
        var storage = new InMemoryEncryptedStorage();
        var client = new FakeOpenAiClient(
        [
            CreateFunctionTurn(
                "call-complete",
                "complete_instruction",
                new JsonObject
                {
                    ["outcome"] = "succeeded",
                    ["summary"] = "The graph-guided destination is visible."
                })
        ]);
        using var service = new SimulatorAgentService(storage, client, new FakeToolGateway());
        service.SetDefaultModelAccessTokenForTesting("sk-local-test");
        var graphId = Guid.NewGuid();
        var versionId = Guid.NewGuid();
        var bindingId = Guid.NewGuid();
        var plan = new SimulatorAgentAppGraphPlan(
            graphId,
            versionId,
            "Open guide",
            "Open a selected area's 3D guide.",
            "3D guide visible",
            [
                new SimulatorAgentAppGraphTransition(
                    1,
                    "open-guide",
                    "Area details",
                    "3D guide visible",
                    "Tap Open 3D Guide",
                    ["The 3D player is visible."],
                    [
                        new SimulatorAgentAppGraphBinding(
                            bindingId,
                            1,
                            "ui_action",
                            new JsonObject { ["action"] = "tap", ["text"] = "Open 3D Guide" },
                            [],
                            ["The 3D player is visible."],
                            0.98m)
                    ])
            ]);

        var result = await service.RunAsync(new SimulatorAgentRunRequest(
            "session-123",
            ["Open the selected area's 3D guide."])
        {
            AppGraphPlans = [plan],
            CaptureTrace = true
        });

        var initialInput = client.Inputs[0].ToJsonString();
        Assert.Contains("Authorized App Graph routes:", initialInput, StringComparison.Ordinal);
        Assert.Contains(graphId.ToString("D"), initialInput, StringComparison.Ordinal);
        Assert.Contains("Area details", initialInput, StringComparison.Ordinal);
        Assert.Contains("3D guide visible", initialInput, StringComparison.Ordinal);
        Assert.Contains("Open 3D Guide", initialInput, StringComparison.Ordinal);
        Assert.DoesNotContain("$GRAPH_PLANS$", initialInput, StringComparison.Ordinal);
        Assert.DoesNotContain("$BINDINGS$", initialInput, StringComparison.Ordinal);
        Assert.Equal(graphId, Assert.Single(result.Audit.AppGraphPlans).GraphId);
        Assert.True(result.Audit.AppGraphEnabled);
        Assert.Contains("Authorized App Graph", result.Audit.AgentPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("report_app_graph_progress", result.Audit.AgentPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_OmitsAppGraphGuidanceWhenPublishedPlanHasNoExecutableBinding()
    {
        var storage = new InMemoryEncryptedStorage();
        var client = new FakeOpenAiClient(
        [
            CreateFunctionTurn(
                "call-complete",
                "complete_instruction",
                new JsonObject
                {
                    ["outcome"] = "succeeded",
                    ["summary"] = "Completed without graph guidance."
                })
        ]);
        using var service = new SimulatorAgentService(storage, client, new FakeToolGateway());
        service.SetDefaultModelAccessTokenForTesting("sk-local-test");
        var plan = new SimulatorAgentAppGraphPlan(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Incomplete guide route",
            "Open a guide.",
            "Guide visible",
            [
                new SimulatorAgentAppGraphTransition(
                    1,
                    "open-guide",
                    "Area details",
                    "Guide visible",
                    "Open the guide",
                    [],
                    [])
            ]);

        var result = await service.RunAsync(new SimulatorAgentRunRequest(
            "session-123",
            ["Open the guide."])
        {
            AppGraphPlans = [plan]
        });

        var initialInput = client.Inputs[0].ToJsonString();
        Assert.DoesNotContain("Authorized published App Graph route candidates", initialInput, StringComparison.Ordinal);
        Assert.DoesNotContain("Incomplete guide route", initialInput, StringComparison.Ordinal);
        Assert.False(result.Audit.AppGraphEnabled);
    }

    [Fact]
    public async Task RunAsync_RequiresCoverageDecisionAfterTaskDiscoveryAndPreservesResidualSteps()
    {
        var storage = new InMemoryEncryptedStorage();
        var client = new FakeOpenAiClient(
        [
            CreateFunctionTurn(
                "call-list",
                "ansight_list_tasks",
                new JsonObject { ["query"] = "open guide" }),
            CreateFunctionTurn(
                "call-run",
                "ansight_run_task",
                new JsonObject { ["taskId"] = "open-guide", ["input"] = new JsonObject() }),
            CreateFunctionTurn(
                "call-complete",
                "complete_instruction",
                new JsonObject
                {
                    ["outcome"] = "succeeded",
                    ["summary"] = "The task completed and its visible result was verified."
                })
        ]);
        var gateway = new FakeToolGateway();
        gateway.Results.Enqueue(new ToolCallResult(
            false,
            "{\"isError\":false,\"result\":{\"matchCount\":1}}",
            "Found one task."));
        gateway.Results.Enqueue(new ToolCallResult(
            false,
            "{\"isError\":false,\"result\":{\"status\":\"passed\"}}",
            "Task passed."));
        using var service = new SimulatorAgentService(storage, client, gateway);
        service.SetDefaultModelAccessTokenForTesting("sk-local-test");

        var result = await service.RunAsync(new SimulatorAgentRunRequest(
            "session-123",
            ["Open the guide and verify the final screen."]));

        Assert.Equal(SimulatorAgentRunStatus.Succeeded, result.Status);
        Assert.Contains("Classify one exact candidate as full coverage", client.Inputs[1].ToJsonString(), StringComparison.Ordinal);
        Assert.Contains("without replaying a passed prefix", client.Requests[0].Instructions, StringComparison.Ordinal);
        Assert.DoesNotContain("If it covers only a prefix", client.Inputs[2].ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_PreloadsRelevantRepositoryTasksAndCanonicalizesTheirCalls()
    {
        const string taskToolName = "ansight_task_1_open_current_area_3d_guide";
        var storage = new InMemoryEncryptedStorage();
        var client = new FakeOpenAiClient(
        [
            CreateFunctionTurn(
                "call-preloaded-task",
                taskToolName,
                new JsonObject { ["targetArea"] = "El Pati" }),
            CreateFunctionTurn(
                "call-complete",
                "complete_instruction",
                new JsonObject
                {
                    ["outcome"] = "succeeded",
                    ["summary"] = "The preloaded repository task opened and verified the El Pati guide."
                })
        ]);
        var shortcut = new RepositoryTaskShortcut(
            taskToolName,
            "open-current-area-3d-guide",
            "Open the current area 3D guide",
            "Starts on the exact current AreaPage and opens its ready 3D guide.",
            "3d-guide",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["targetArea"] = new JsonObject { ["type"] = "string" }
                },
                ["required"] = new JsonArray("targetArea"),
                ["additionalProperties"] = false
            },
            72.5,
            0.75);
        var gateway = new FakeToolGateway
        {
            RepositoryTaskShortcuts = [shortcut]
        };
        gateway.Results.Enqueue(new ToolCallResult(
            false,
            "{\"isError\":false,\"result\":{\"taskId\":\"open-current-area-3d-guide\",\"status\":\"Passed\"}}",
            "Task passed."));
        using var service = new SimulatorAgentService(storage, client, gateway);
        service.SetDefaultModelAccessTokenForTesting("sk-local-test");

        var result = await service.RunAsync(new SimulatorAgentRunRequest(
            "session-123",
            ["Open and verify the El Pati 3D guide."]));

        Assert.Equal(SimulatorAgentRunStatus.Succeeded, result.Status);
        Assert.Contains(
            client.Tools[0].OfType<JsonObject>(),
            definition => definition["name"]?.GetValue<string>() == taskToolName);
        var initialInput = client.Inputs[0].ToJsonString();
        Assert.Contains("Preloaded tasks:", initialInput, StringComparison.Ordinal);
        Assert.Contains("open-current-area-3d-guide", initialInput, StringComparison.Ordinal);
        Assert.DoesNotContain("No earlier instructions", initialInput, StringComparison.Ordinal);
        Assert.DoesNotContain("Authorized published App Graph route candidates", initialInput, StringComparison.Ordinal);
        Assert.DoesNotContain("Host-owned App Graph exploration frontier", initialInput, StringComparison.Ordinal);
        var canonicalCall = Assert.Single(gateway.Calls);
        Assert.Equal("ansight_run_task", canonicalCall.ToolName);
        Assert.Equal("open-current-area-3d-guide", canonicalCall.Arguments["taskId"]?.GetValue<string>());
        Assert.Equal("El Pati", canonicalCall.Arguments["input"]?["targetArea"]?.GetValue<string>());
        Assert.Contains("without replaying a passed prefix", client.Requests[0].Instructions, StringComparison.Ordinal);
        Assert.DoesNotContain("If it covers only a prefix", client.Inputs[1].ToJsonString(), StringComparison.Ordinal);
        Assert.Contains(
            result.Audit.ToolCalls,
            call => call.ToolName == "ansight_run_task" && !call.IsError);
    }

    [Fact]
    public async Task RunAsync_BlocksInitialManualUiUntilPreloadedTaskIsUsedOrReassessed()
    {
        const string taskToolName = "ansight_task_1_open_my_account_from_map_menu";
        var storage = new InMemoryEncryptedStorage();
        var client = new FakeOpenAiClient(
        [
            CreateFunctionTurn(
                "call-blocked-tap",
                "ansight_tap_ui",
                new JsonObject { ["automationId"] = "home-menu-item-button-account" }),
            CreateFunctionTurn(
                "call-preloaded-task",
                taskToolName,
                new JsonObject()),
            CreateFunctionTurn(
                "call-complete",
                "complete_instruction",
                new JsonObject
                {
                    ["outcome"] = "succeeded",
                    ["summary"] = "The preloaded task opened My Account."
                })
        ]);
        var gateway = new FakeToolGateway
        {
            RepositoryTaskShortcuts =
            [
                CreateRepositoryTaskShortcut(
                    taskToolName,
                    "open-my-account-from-map-menu",
                    "Open My Account from the map menu")
            ]
        };
        gateway.Results.Enqueue(new ToolCallResult(
            false,
            "{\"isError\":false,\"result\":{\"taskId\":\"open-my-account-from-map-menu\",\"status\":\"Passed\"}}",
            "Task passed."));
        using var service = new SimulatorAgentService(storage, client, gateway);
        service.SetDefaultModelAccessTokenForTesting("sk-local-test");

        var result = await service.RunAsync(new SimulatorAgentRunRequest(
            "session-123",
            ["Open My Account from the map menu."]));

        Assert.Equal(SimulatorAgentRunStatus.Succeeded, result.Status);
        var blockedTap = Assert.Single(
            result.Audit.ToolCalls,
            call => call.CallId == "call-blocked-tap");
        Assert.True(blockedTap.IsError);
        Assert.Contains("open-my-account-from-map-menu", blockedTap.Message, StringComparison.Ordinal);
        var canonicalCall = Assert.Single(gateway.Calls);
        Assert.Equal("ansight_run_task", canonicalCall.ToolName);
        Assert.Equal("open-my-account-from-map-menu", canonicalCall.Arguments["taskId"]?.GetValue<string>());
    }

    [Fact]
    public async Task RunAsync_BlocksManualUiUntilUnusedTaskShortcutsAreExplicitlyReassessed()
    {
        const string firstTaskToolName = "ansight_task_1_focus_map_search_area";
        const string remainingTaskToolName = "ansight_task_2_open_current_area_3d_guide";
        var storage = new InMemoryEncryptedStorage();
        var client = new FakeOpenAiClient(
        [
            CreateFunctionTurn(
                "call-first-task",
                firstTaskToolName,
                new JsonObject { ["searchArea"] = "Siurana" }),
            CreateFunctionTurn(
                "call-blocked-tap",
                "ansight_tap_ui",
                new JsonObject { ["automationId"] = "area-open3d-guide-button" }),
            CreateFunctionTurn(
                "call-declare-uncovered",
                "ansight_declare_uncovered_step",
                new JsonObject
                {
                    ["uncoveredStep"] = "Open an app control that no remaining task covers.",
                    ["reason"] = "no-matching-task",
                    ["relatedTaskId"] = null,
                    ["evidence"] = "The remaining task opens a 3D guide and does not cover this unrelated control.",
                    ["consideredTaskIds"] = new JsonArray("open-current-area-3d-guide")
                }),
            CreateFunctionTurn(
                "call-allowed-tap",
                "ansight_tap_ui",
                new JsonObject { ["automationId"] = "area-open3d-guide-button" }),
            CreateFunctionTurn(
                "call-complete",
                "complete_instruction",
                new JsonObject
                {
                    ["outcome"] = "succeeded",
                    ["summary"] = "The declared uncovered manual step completed."
                })
        ]);
        var gateway = new FakeToolGateway
        {
            RepositoryTaskShortcuts =
            [
                CreateRepositoryTaskShortcut(
                    firstTaskToolName,
                    "focus-map-search-area",
                    "Focus an exact map search area"),
                CreateRepositoryTaskShortcut(
                    remainingTaskToolName,
                    "open-current-area-3d-guide",
                    "Open the current area 3D guide")
            ]
        };
        gateway.Results.Enqueue(new ToolCallResult(
            false,
            "{\"isError\":false,\"result\":{\"taskId\":\"focus-map-search-area\",\"status\":\"Passed\"}}",
            "Task passed."));
        gateway.Results.Enqueue(new ToolCallResult(
            false,
            "{\"isError\":false,\"result\":{\"performed\":true}}",
            "Tap delivered."));
        using var service = new SimulatorAgentService(storage, client, gateway);
        service.SetDefaultModelAccessTokenForTesting("sk-local-test");

        var result = await service.RunAsync(new SimulatorAgentRunRequest(
            "session-123",
            ["Use repository tasks, then perform only an uncovered residual step."]));

        Assert.Equal(SimulatorAgentRunStatus.Succeeded, result.Status);
        Assert.Contains(
            client.Tools[0].OfType<JsonObject>(),
            definition => definition["name"]?.GetValue<string>() == "ansight_declare_uncovered_step");
        Assert.Equal(2, gateway.Calls.Count);
        Assert.Equal("ansight_run_task", gateway.Calls[0].ToolName);
        Assert.Equal("ansight_tap_ui", gateway.Calls[1].ToolName);
        var blockedTap = Assert.Single(
            result.Audit.ToolCalls,
            call => call.CallId == "call-blocked-tap");
        Assert.True(blockedTap.IsError);
        Assert.Contains("open-current-area-3d-guide", blockedTap.Message, StringComparison.Ordinal);
        var declaration = Assert.Single(
            result.Audit.ToolCalls,
            call => call.CallId == "call-declare-uncovered");
        Assert.False(declaration.IsError);
        var allowedTap = Assert.Single(
            result.Audit.ToolCalls,
            call => call.CallId == "call-allowed-tap");
        Assert.False(allowedTap.IsError);
        Assert.Contains(
            "reassess every unused preloaded task before further manual UI",
            client.Requests[0].Instructions,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_RejectsNoMatchingTaskDeclarationWhenUnusedShortcutStronglyMatchesStep()
    {
        const string firstTaskToolName = "ansight_task_1_focus_map_search_area";
        const string remainingTaskToolName = "ansight_task_2_open_current_area_3d_guide";
        var storage = new InMemoryEncryptedStorage();
        var client = new FakeOpenAiClient(
        [
            CreateFunctionTurn(
                "call-first-task",
                firstTaskToolName,
                new JsonObject { ["searchArea"] = "Siurana" }),
            CreateFunctionTurn(
                "call-false-uncovered",
                "ansight_declare_uncovered_step",
                new JsonObject
                {
                    ["uncoveredStep"] = "Open and verify El Pati's 3D guide.",
                    ["reason"] = "no-matching-task",
                    ["relatedTaskId"] = null,
                    ["evidence"] = "The remaining task was compared with the requested transition.",
                    ["consideredTaskIds"] = new JsonArray("open-current-area-3d-guide")
                }),
            CreateFunctionTurn(
                "call-matching-task",
                remainingTaskToolName,
                new JsonObject { ["targetArea"] = "El Pati" }),
            CreateFunctionTurn(
                "call-complete",
                "complete_instruction",
                new JsonObject
                {
                    ["outcome"] = "succeeded",
                    ["summary"] = "The matching task opened the El Pati 3D guide."
                })
        ]);
        var gateway = new FakeToolGateway
        {
            RepositoryTaskShortcuts =
            [
                CreateRepositoryTaskShortcut(
                    firstTaskToolName,
                    "focus-map-search-area",
                    "Focus an exact map search area"),
                new RepositoryTaskShortcut(
                    remainingTaskToolName,
                    "open-current-area-3d-guide",
                    "Open and validate the current area 3D guide",
                    "Opens the 3D guide from the current named AreaPage and verifies the ready viewer.",
                    "3d-guide",
                    new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["targetArea"] = new JsonObject { ["type"] = "string" }
                        },
                        ["required"] = new JsonArray("targetArea"),
                        ["additionalProperties"] = false
                    },
                    100,
                    1)
            ]
        };
        gateway.Results.Enqueue(new ToolCallResult(
            false,
            "{\"isError\":false,\"result\":{\"taskId\":\"focus-map-search-area\",\"status\":\"Passed\"}}",
            "Task passed."));
        gateway.Results.Enqueue(new ToolCallResult(
            false,
            "{\"isError\":false,\"result\":{\"taskId\":\"open-current-area-3d-guide\",\"status\":\"Passed\"}}",
            "Task passed."));
        using var service = new SimulatorAgentService(storage, client, gateway);
        service.SetDefaultModelAccessTokenForTesting("sk-local-test");

        var result = await service.RunAsync(new SimulatorAgentRunRequest(
            "session-123",
            ["Use repository tasks to open and verify the El Pati 3D guide."]));

        Assert.Equal(SimulatorAgentRunStatus.Succeeded, result.Status);
        var rejectedDeclaration = Assert.Single(
            result.Audit.ToolCalls,
            call => call.CallId == "call-false-uncovered");
        Assert.True(rejectedDeclaration.IsError);
        Assert.Contains("strongly matches", rejectedDeclaration.Message, StringComparison.Ordinal);
        Assert.Contains("open-current-area-3d-guide", rejectedDeclaration.Message, StringComparison.Ordinal);
        Assert.Equal(2, gateway.Calls.Count);
        Assert.All(gateway.Calls, call => Assert.Equal("ansight_run_task", call.ToolName));
    }

    [Fact]
    public async Task RunAsync_RejectsPartialResidualDeclarationWhenUnusedShortcutStronglyMatchesStep()
    {
        const string firstTaskToolName = "ansight_task_1_open_area_details";
        const string remainingTaskToolName = "ansight_task_2_open_current_area_3d_guide";
        var storage = new InMemoryEncryptedStorage();
        var client = new FakeOpenAiClient(
        [
            CreateFunctionTurn(
                "call-first-task",
                firstTaskToolName,
                new JsonObject()),
            CreateFunctionTurn(
                "call-false-partial",
                "ansight_declare_uncovered_step",
                new JsonObject
                {
                    ["uncoveredStep"] = "Open and verify El Pati's 3D guide.",
                    ["reason"] = "partial-task-residual",
                    ["relatedTaskId"] = "open-area-details",
                    ["evidence"] = "The details task completed and the guide still needs to be opened.",
                    ["consideredTaskIds"] = new JsonArray("open-current-area-3d-guide")
                }),
            CreateFunctionTurn(
                "call-matching-task",
                remainingTaskToolName,
                new JsonObject()),
            CreateFunctionTurn(
                "call-complete",
                "complete_instruction",
                new JsonObject
                {
                    ["outcome"] = "succeeded",
                    ["summary"] = "The matching task opened the guide."
                })
        ]);
        var gateway = new FakeToolGateway
        {
            RepositoryTaskShortcuts =
            [
                CreateRepositoryTaskShortcut(
                    firstTaskToolName,
                    "open-area-details",
                    "Open area details"),
                CreateRepositoryTaskShortcut(
                    remainingTaskToolName,
                    "open-current-area-3d-guide",
                    "Open and verify the current area 3D guide")
            ]
        };
        gateway.Results.Enqueue(new ToolCallResult(
            false,
            "{\"isError\":false,\"result\":{\"taskId\":\"open-area-details\",\"status\":\"Passed\"}}",
            "Task passed."));
        gateway.Results.Enqueue(new ToolCallResult(
            false,
            "{\"isError\":false,\"result\":{\"taskId\":\"open-current-area-3d-guide\",\"status\":\"Passed\"}}",
            "Task passed."));
        using var service = new SimulatorAgentService(storage, client, gateway);
        service.SetDefaultModelAccessTokenForTesting("sk-local-test");

        var result = await service.RunAsync(new SimulatorAgentRunRequest(
            "session-123",
            ["Use repository tasks to open and verify the El Pati 3D guide."]));

        Assert.Equal(SimulatorAgentRunStatus.Succeeded, result.Status);
        var rejectedDeclaration = Assert.Single(
            result.Audit.ToolCalls,
            call => call.CallId == "call-false-partial");
        Assert.True(rejectedDeclaration.IsError);
        Assert.Contains("strongly matches", rejectedDeclaration.Message, StringComparison.Ordinal);
        Assert.Equal(2, gateway.Calls.Count);
        Assert.All(gateway.Calls, call => Assert.Equal("ansight_run_task", call.ToolName));
    }

    [Fact]
    public async Task RunAsync_RequiresTaskFailedDeclarationBeforeManualUiAfterTaskFailure()
    {
        const string taskToolName = "ansight_task_1_open_current_area_3d_guide";
        var storage = new InMemoryEncryptedStorage();
        var client = new FakeOpenAiClient(
        [
            CreateFunctionTurn("call-task", taskToolName, new JsonObject()),
            CreateFunctionTurn(
                "call-blocked-tap",
                "ansight_tap_ui",
                new JsonObject { ["automationId"] = "area-open3d-guide-button" }),
            CreateFunctionTurn(
                "call-declare-failed",
                "ansight_declare_uncovered_step",
                new JsonObject
                {
                    ["uncoveredStep"] = "Open the guide manually after the task failed before changing UI state.",
                    ["reason"] = "task-failed",
                    ["relatedTaskId"] = "open-current-area-3d-guide",
                    ["evidence"] = "The host-recorded task attempt failed before the guide was opened.",
                    ["consideredTaskIds"] = new JsonArray()
                }),
            CreateFunctionTurn(
                "call-allowed-tap",
                "ansight_tap_ui",
                new JsonObject { ["automationId"] = "area-open3d-guide-button" }),
            CreateFunctionTurn(
                "call-complete",
                "complete_instruction",
                new JsonObject
                {
                    ["outcome"] = "succeeded",
                    ["summary"] = "The declared residual action completed."
                })
        ]);
        var gateway = new FakeToolGateway
        {
            RepositoryTaskShortcuts =
            [
                CreateRepositoryTaskShortcut(
                    taskToolName,
                    "open-current-area-3d-guide",
                    "Open current area 3D guide")
            ]
        };
        gateway.Results.Enqueue(new ToolCallResult(
            true,
            "{\"isError\":true,\"message\":\"Task assertion failed.\"}",
            "Task assertion failed."));
        gateway.Results.Enqueue(new ToolCallResult(
            false,
            "{\"isError\":false,\"performed\":true}",
            "Tap delivered."));
        using var service = new SimulatorAgentService(storage, client, gateway);
        service.SetDefaultModelAccessTokenForTesting("sk-local-test");

        var result = await service.RunAsync(new SimulatorAgentRunRequest(
            "session-123",
            ["Open the current area 3D guide."]));

        Assert.Equal(SimulatorAgentRunStatus.Succeeded, result.Status);
        var blockedTap = Assert.Single(
            result.Audit.ToolCalls,
            call => call.CallId == "call-blocked-tap");
        Assert.True(blockedTap.IsError);
        Assert.Contains("Failed task IDs", blockedTap.Message, StringComparison.Ordinal);
        var declaration = Assert.Single(
            result.Audit.ToolCalls,
            call => call.CallId == "call-declare-failed");
        Assert.False(declaration.IsError);
        var allowedTap = Assert.Single(
            result.Audit.ToolCalls,
            call => call.CallId == "call-allowed-tap");
        Assert.False(allowedTap.IsError);
        Assert.Collection(
            gateway.Calls,
            call => Assert.Equal("ansight_run_task", call.ToolName),
            call => Assert.Equal("ansight_tap_ui", call.ToolName));
    }

    [Fact]
    public async Task RunAsync_ReassessesRepositoryTasksAfterEachDeclaredManualMutation()
    {
        const string remainingTaskId = "export-account-data";
        var storage = new InMemoryEncryptedStorage();
        var client = new FakeOpenAiClient(
        [
            CreateFunctionTurn(
                "call-first-declaration",
                "ansight_declare_uncovered_step",
                NoMatchingTaskDeclaration("Dismiss the welcome overlay.", remainingTaskId)),
            CreateFunctionTurn(
                "call-first-tap",
                "ansight_tap_ui",
                new JsonObject { ["automationId"] = "welcome-dismiss-button" }),
            CreateFunctionTurn(
                "call-blocked-second-tap",
                "ansight_tap_ui",
                new JsonObject { ["automationId"] = "consent-accept-button" }),
            CreateFunctionTurn(
                "call-second-declaration",
                "ansight_declare_uncovered_step",
                NoMatchingTaskDeclaration("Accept the consent prompt.", remainingTaskId)),
            CreateFunctionTurn(
                "call-second-tap",
                "ansight_tap_ui",
                new JsonObject { ["automationId"] = "consent-accept-button" }),
            CreateFunctionTurn(
                "call-complete",
                "complete_instruction",
                new JsonObject
                {
                    ["outcome"] = "succeeded",
                    ["summary"] = "Both declared manual steps completed."
                })
        ]);
        var gateway = new FakeToolGateway
        {
            RepositoryTaskShortcuts =
            [
                CreateRepositoryTaskShortcut(
                    "ansight_task_1_export_account_data",
                    remainingTaskId,
                    "Export account data")
            ]
        };
        gateway.Results.Enqueue(new ToolCallResult(
            false,
            "{\"isError\":false,\"performed\":true}",
            "Tap delivered."));
        gateway.Results.Enqueue(new ToolCallResult(
            false,
            "{\"isError\":false,\"performed\":true}",
            "Tap delivered."));
        using var service = new SimulatorAgentService(storage, client, gateway);
        service.SetDefaultModelAccessTokenForTesting("sk-local-test");

        var result = await service.RunAsync(new SimulatorAgentRunRequest(
            "session-123",
            ["Dismiss the welcome overlay and accept the consent prompt."]));

        Assert.Equal(SimulatorAgentRunStatus.Succeeded, result.Status);
        var blockedTap = Assert.Single(
            result.Audit.ToolCalls,
            call => call.CallId == "call-blocked-second-tap");
        Assert.True(blockedTap.IsError);
        Assert.Equal(2, gateway.Calls.Count);
        Assert.All(gateway.Calls, call => Assert.Equal("ansight_tap_ui", call.ToolName));

        static JsonObject NoMatchingTaskDeclaration(string step, string taskId)
            => new()
            {
                ["uncoveredStep"] = step,
                ["reason"] = "no-matching-task",
                ["relatedTaskId"] = null,
                ["evidence"] = "The export task does not cover this UI transition.",
                ["consideredTaskIds"] = new JsonArray(taskId)
            };
    }

    [Fact]
    public async Task RunAsync_OmitsLifecycleToolsForForegroundNonLifecycleInstruction()
    {
        var storage = new InMemoryEncryptedStorage();
        var client = new FakeOpenAiClient(
        [
            CreateFunctionTurn(
                "call-complete",
                "complete_instruction",
                new JsonObject
                {
                    ["outcome"] = "succeeded",
                    ["summary"] = "The current guide is visible."
                })
        ]);
        var gateway = CreateGatewayWithLifecycleTools();
        using var service = new SimulatorAgentService(storage, client, gateway);
        service.SetDefaultModelAccessTokenForTesting("sk-local-test");

        await service.RunAsync(new SimulatorAgentRunRequest(
            "session-123",
            ["Open and verify the current 3D guide."]));

        Assert.DoesNotContain(
            client.Tools[0].OfType<JsonObject>(),
            tool => IsLifecycleTool(tool["name"]?.GetValue<string>()));
    }

    [Fact]
    public async Task RunAsync_RetainsOnlyRequestedLifecycleToolsForForegroundSession()
    {
        var storage = new InMemoryEncryptedStorage();
        var client = new FakeOpenAiClient(
        [
            CreateFunctionTurn(
                "call-complete",
                "complete_instruction",
                new JsonObject
                {
                    ["outcome"] = "succeeded",
                    ["summary"] = "The app was ready for relaunch."
                })
        ]);
        var gateway = CreateGatewayWithLifecycleTools();
        using var service = new SimulatorAgentService(storage, client, gateway);
        service.SetDefaultModelAccessTokenForTesting("sk-local-test");

        await service.RunAsync(new SimulatorAgentRunRequest(
            "session-123",
            ["Relaunch the app and verify it recovers."]));

        var toolNames = client.Tools[0]
            .OfType<JsonObject>()
            .Select(tool => tool["name"]?.GetValue<string>())
            .ToArray();
        Assert.Contains("ansight_launch_app", toolNames);
        Assert.Contains("ansight_terminate_app", toolNames);
        Assert.DoesNotContain("ansight_list_host_devices", toolNames);
        Assert.DoesNotContain("ansight_start_device", toolNames);
    }

    [Fact]
    public async Task RunAsync_RebindsToReplacementSessionAfterRelaunch()
    {
        var storage = new InMemoryEncryptedStorage();
        var client = new FakeOpenAiClient(
        [
            CreateFunctionTurn("call-launch", "ansight_launch_app", new JsonObject()),
            CreateFunctionTurn("call-observe", "ansight_get_live_visual_tree", new JsonObject()),
            CreateFunctionTurn(
                "call-complete",
                "complete_instruction",
                new JsonObject
                {
                    ["outcome"] = "succeeded",
                    ["summary"] = "Recovered after relaunch."
                })
        ]);
        var gateway = new FakeToolGateway
        {
            ReplacementSession = new ToolSessionContext(
                "session-456",
                "com.example.app",
                "Example App",
                IsLive: true,
                AppState: "foreground",
                Device: new SimulatorAgentRunDevice(
                    "test-device-123",
                    "Apple",
                    "iPhone 16 Pro",
                    "phone",
                    "iOS",
                    "18.6",
                    IsVirtual: true,
                    IsEmulator: true))
        };
        using var service = new SimulatorAgentService(storage, client, gateway);
        service.SetDefaultModelAccessTokenForTesting("sk-local-test");

        var result = await service.RunAsync(new SimulatorAgentRunRequest(
            "session-123",
            ["Recover the app and inspect it."])
        {
            TargetDeviceIdentifier = "test-device-123"
        });

        Assert.Equal(SimulatorAgentRunStatus.Succeeded, result.Status);
        Assert.Equal("session-456", result.Audit.SessionId);
        Assert.Equal(["session-456"], gateway.ReboundSessionIds);
        Assert.Equal("session-123", gateway.Calls[0].SessionId);
        Assert.Equal("session-456", gateway.Calls[1].SessionId);
        Assert.Contains("sessionRebound", client.Inputs[1].ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_AllowsCompletionOnlyPassAfterSuccessfulFinalWorkTurn()
    {
        var storage = new InMemoryEncryptedStorage();
        var client = new FakeOpenAiClient(
        [
            CreateFunctionTurn(
                "call-verify",
                "ansight_assert_ui",
                new JsonObject { ["text"] = "Ready" }),
            CreateFunctionTurn(
                "call-complete",
                "complete_instruction",
                new JsonObject
                {
                    ["outcome"] = "succeeded",
                    ["summary"] = "Ready is visible."
                })
        ]);
        using var service = new SimulatorAgentService(storage, client, new FakeToolGateway());
        service.SetDefaultModelAccessTokenForTesting("sk-local-test");

        var result = await service.RunAsync(new SimulatorAgentRunRequest(
            "session-123",
            ["Verify Ready is visible."],
            MaximumTurnsPerInstruction: 1));

        Assert.Equal(SimulatorAgentRunStatus.Succeeded, result.Status);
        Assert.Equal(2, result.TotalTurns);
        Assert.Equal(2, result.Instructions[0].Turns);
        Assert.Equal(1, result.Audit.CompletionGracePassCount);
        Assert.Contains("completion-only pass", client.Inputs[1].ToJsonString(), StringComparison.Ordinal);
        var completionTools = Assert.Single(client.Tools[1].OfType<JsonObject>());
        Assert.Equal("complete_instruction", completionTools["name"]?.GetValue<string>());
    }

    [Fact]
    public async Task RunAsync_TurnLimitReportsExactLastToolFailure()
    {
        var storage = new InMemoryEncryptedStorage();
        var client = new FakeOpenAiClient(
        [
            CreateFunctionTurn(
                "call-tap",
                "ansight_tap_ui",
                new JsonObject
                {
                    ["automationId"] = "continue-button"
                })
        ]);
        var gateway = new FakeToolGateway
        {
            Result = new ToolCallResult(
                true,
                "{\"isError\":true}",
                "The selected UI node does not expose usable screen-space bounds.")
        };
        var reported = new List<SimulatorAgentProgress>();
        using var service = new SimulatorAgentService(storage, client, gateway);
        service.SetDefaultModelAccessTokenForTesting("sk-local-test");

        var result = await service.RunAsync(
            new SimulatorAgentRunRequest(
                "session-123",
                ["Tap Continue."],
                MaximumTurnsPerInstruction: 1),
            new SynchronousProgress<SimulatorAgentProgress>(reported.Add));

        Assert.Equal(SimulatorAgentRunStatus.Failed, result.Status);
        Assert.Contains("ansight_tap_ui", result.Message);
        Assert.Contains("screen-space bounds", result.Message);
        Assert.Contains(reported, progress =>
            progress.Message.Contains("continue-button", StringComparison.Ordinal));
        Assert.Contains(reported, progress =>
            progress.Message.Contains("screen-space bounds", StringComparison.Ordinal));
        Assert.Contains(reported, progress =>
            progress.Stage == SimulatorAgentProgressStage.ModelCompleted
            && progress.Message.Contains("15 total", StringComparison.Ordinal));
        Assert.All(reported, progress =>
            Assert.InRange(progress.InstructionIndex, 1, progress.InstructionCount));
        var auditedToolCall = Assert.Single(
            result.Audit.ToolCalls,
            static call => call.CallId == "call-tap");
        Assert.True(auditedToolCall.IsError);
        Assert.Contains("screen-space bounds", auditedToolCall.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_StopsReadOnlyObservationLoopAfterWarning()
    {
        var storage = new InMemoryEncryptedStorage();
        var client = new FakeOpenAiClient(
            Enumerable.Range(1, 9).Select(index => CreateFunctionTurn(
                $"call-observe-{index}",
                "ansight_get_live_visual_tree",
                new JsonObject { ["maxNodes"] = 100 + index })));
        var gateway = new FakeToolGateway();
        using var service = new SimulatorAgentService(storage, client, gateway);
        service.SetDefaultModelAccessTokenForTesting("sk-local-test");

        var result = await service.RunAsync(new SimulatorAgentRunRequest(
            "session-123",
            ["Select the exact map annotation."],
            MaximumTurnsPerInstruction: 12)
        {
            CaptureTrace = true
        });

        Assert.Equal(SimulatorAgentRunStatus.Failed, result.Status);
        var instruction = Assert.Single(result.Instructions);
        Assert.Contains("8 consecutive read-only", instruction.Summary, StringComparison.Ordinal);
        Assert.Equal(9, instruction.Turns);
        Assert.Equal(8, gateway.Calls.Count);
        Assert.Contains("next call must perform", client.Inputs[8].ToJsonString(), StringComparison.Ordinal);
        var stoppedCall = Assert.Single(
            result.Audit.ToolCalls,
            static call => call.CallId == "call-observe-9");
        Assert.True(stoppedCall.IsError);
        Assert.Contains("stagnation", stoppedCall.Result.Content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunAsync_ContinuesRemainingInstructionsAfterModelDeclaredFailure()
    {
        var storage = new InMemoryEncryptedStorage();
        var client = new FakeOpenAiClient(
        [
            CreateFunctionTurn(
                "call-failed",
                "complete_instruction",
                new JsonObject
                {
                    ["outcome"] = "failed",
                    ["summary"] = "The first assertion did not match."
                }),
            CreateFunctionTurn(
                "call-succeeded",
                "complete_instruction",
                new JsonObject
                {
                    ["outcome"] = "succeeded",
                    ["summary"] = "The second assertion matched."
                })
        ]);
        using var service = new SimulatorAgentService(storage, client, new FakeToolGateway());
        service.SetDefaultModelAccessTokenForTesting("sk-local-test");

        var result = await service.RunAsync(new SimulatorAgentRunRequest(
            "session-123",
            ["Check the first state.", "Check the second state."]));

        Assert.Equal(SimulatorAgentRunStatus.Failed, result.Status);
        Assert.Equal(2, result.Instructions.Count);
        Assert.Equal(SimulatorAgentInstructionStatus.Failed, result.Instructions[0].Status);
        Assert.Equal(SimulatorAgentInstructionStatus.Succeeded, result.Instructions[1].Status);
        Assert.Equal(2, result.TotalTurns);
        Assert.Contains("all 2 instruction", result.Message, StringComparison.Ordinal);
        Assert.Contains("[Failed]: The first assertion did not match.", client.Inputs[1].ToJsonString());
        Assert.True(result.Audit.ContinueAfterInstructionFailure);
    }

    [Fact]
    public async Task RunAsync_PromptsForImmediateDecisionAfterAuthoritativeAppToolResult()
    {
        var storage = new InMemoryEncryptedStorage();
        var client = new FakeOpenAiClient(
        [
            CreateFunctionTurn(
                "call-app-tool",
                "ansight_call_app_tool",
                new JsonObject
                {
                    ["toolId"] = "example.query_contents",
                    ["arguments"] = new JsonObject()
                }),
            CreateFunctionTurn(
                "call-complete",
                "complete_instruction",
                new JsonObject
                {
                    ["outcome"] = "failed",
                    ["summary"] = "The required literal value is absent."
                })
        ]);
        using var service = new SimulatorAgentService(storage, client, new FakeToolGateway());
        service.SetDefaultModelAccessTokenForTesting("sk-local-test");

        var result = await service.RunAsync(new SimulatorAgentRunRequest(
            "session-123",
            ["Validate the required literal value."]));

        Assert.Equal(SimulatorAgentRunStatus.Failed, result.Status);
        Assert.Contains(
            "call complete_instruction immediately",
            client.Inputs[1].ToJsonString(),
            StringComparison.Ordinal);
        Assert.Contains(
            "must not mutate the UI",
            client.Inputs[1].ToJsonString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_TreatsAuthoritativeUnmetStateAsActionPrecondition()
    {
        var storage = new InMemoryEncryptedStorage();
        var client = new FakeOpenAiClient(
        [
            CreateFunctionTurn(
                "query-selection",
                "ansight_call_app_tool",
                new JsonObject
                {
                    ["toolId"] = "example.query_selection",
                    ["arguments"] = new JsonObject()
                }),
            CreateFunctionTurn(
                "complete-selection",
                "complete_instruction",
                new JsonObject
                {
                    ["outcome"] = "succeeded",
                    ["summary"] = "The exact target was selected and verified."
                })
        ]);
        using var service = new SimulatorAgentService(storage, client, new FakeToolGateway());
        service.SetDefaultModelAccessTokenForTesting("sk-local-test");

        var result = await service.RunAsync(new SimulatorAgentRunRequest(
            "session-123",
            ["Select Secret Garden on the map."]));

        Assert.Equal(SimulatorAgentRunStatus.Succeeded, result.Status);
        Assert.Contains(
            "an unmet state is a precondition",
            client.Inputs[1].ToJsonString(),
            StringComparison.Ordinal);
        Assert.Contains(
            "Never act on a different entity or substitute an identifier",
            client.Inputs[1].ToJsonString(),
            StringComparison.Ordinal);
        Assert.DoesNotContain("map-dismiss-search-button", client.Inputs[1].ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_ReusesSuccessfulAppToolCallInLaterInstructionContext()
    {
        var storage = new InMemoryEncryptedStorage();
        var client = new FakeOpenAiClient(
        [
            CreateFunctionTurn(
                "query-map",
                "ansight_call_app_tool",
                new JsonObject
                {
                    ["toolId"] = "redpoint.mapbox.query_surface_contents",
                    ["arguments"] = new JsonObject
                    {
                        ["annotationKind"] = "point",
                        ["maxAnnotations"] = 100
                    }
                }),
            CreateFunctionTurn(
                "complete-map-check",
                "complete_instruction",
                new JsonObject
                {
                    ["outcome"] = "succeeded",
                    ["summary"] = "The map contents were validated."
                }),
            CreateFunctionTurn(
                "complete-map-action",
                "complete_instruction",
                new JsonObject
                {
                    ["outcome"] = "succeeded",
                    ["summary"] = "The exact map target was selected."
                })
        ]);
        using var service = new SimulatorAgentService(storage, client, new FakeToolGateway());
        service.SetDefaultModelAccessTokenForTesting("sk-local-test");

        var result = await service.RunAsync(new SimulatorAgentRunRequest(
            "session-123",
            ["Validate the map contents.", "Select the exact map target."]));

        Assert.Equal(SimulatorAgentRunStatus.Succeeded, result.Status);
        var secondInstructionInput = client.Inputs[2].ToJsonString();
        Assert.Contains("These app-tool calls succeeded earlier", secondInstructionInput, StringComparison.Ordinal);
        Assert.Contains("redpoint.mapbox.query_surface_contents", secondInstructionInput, StringComparison.Ordinal);
        Assert.Contains("annotationKind", secondInstructionInput, StringComparison.Ordinal);
        Assert.Contains("reuse their exact IDs and argument shapes", secondInstructionInput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_ReportsAuthoritativeCameraChangeAfterSwipe()
    {
        var storage = new InMemoryEncryptedStorage();
        var client = new FakeOpenAiClient(
        [
            CreateFunctionTurn(
                "query-camera-before",
                "ansight_call_app_tool",
                new JsonObject
                {
                    ["toolId"] = "redpoint.3d_player.query_view",
                    ["arguments"] = new JsonObject { ["includeScene"] = true }
                }),
            CreateFunctionTurn(
                "swipe-player",
                "ansight_swipe_ui",
                new JsonObject
                {
                    ["type"] = "EvergineView",
                    ["orientation"] = "W",
                    ["length"] = 0.35,
                    ["durationMs"] = 400
                }),
            CreateFunctionTurn(
                "query-camera-after",
                "ansight_call_app_tool",
                new JsonObject
                {
                    ["toolId"] = "redpoint.3d_player.query_view",
                    ["arguments"] = new JsonObject { ["includeScene"] = true }
                }),
            CreateFunctionTurn(
                "complete-swipe",
                "complete_instruction",
                new JsonObject
                {
                    ["outcome"] = "succeeded",
                    ["summary"] = "The camera changed after the swipe."
                })
        ]);
        var gateway = new FakeToolGateway();
        gateway.Results.Enqueue(CreateCameraToolResult(10));
        gateway.Results.Enqueue(new ToolCallResult(false, "{\"isError\":false}", "Swiped."));
        gateway.Results.Enqueue(CreateCameraToolResult(25));
        using var service = new SimulatorAgentService(storage, client, gateway);
        service.SetDefaultModelAccessTokenForTesting("sk-local-test");

        var result = await service.RunAsync(new SimulatorAgentRunRequest(
            "session-123",
            ["Validate a swipe changes the camera angle."]));

        Assert.Equal(SimulatorAgentRunStatus.Succeeded, result.Status);
        Assert.Contains("cameraChanged=true", client.Inputs[3].ToJsonString(), StringComparison.Ordinal);
        Assert.Contains("complete_instruction now", client.Inputs[3].ToJsonString(), StringComparison.Ordinal);
        Assert.Single(gateway.Calls, static call => call.ToolName == "ansight_swipe_ui");
    }

    [Fact]
    public async Task RunAsync_RequestsInstructionSpecificVerificationAfterBoundedTargetTap()
    {
        var storage = new InMemoryEncryptedStorage();
        var client = new FakeOpenAiClient(
        [
            CreateFunctionTurn(
                "tap-map-point",
                "ansight_tap_ui",
                new JsonObject
                {
                    ["type"] = "MapboxView",
                    ["targetX"] = 530.15,
                    ["targetY"] = 690.96
                }),
            CreateFunctionTurn(
                "complete-selection",
                "complete_instruction",
                new JsonObject
                {
                    ["outcome"] = "succeeded",
                    ["summary"] = "The selection card appeared."
                })
        ]);
        using var service = new SimulatorAgentService(storage, client, new FakeToolGateway());
        service.SetDefaultModelAccessTokenForTesting("sk-local-test");

        var result = await service.RunAsync(new SimulatorAgentRunRequest(
            "session-123",
            ["Select the exact map annotation."]));

        Assert.Equal(SimulatorAgentRunStatus.Succeeded, result.Status);
        var postTapInput = client.Inputs[1].ToJsonString();
        Assert.Contains("bounded target-local tap was delivered", postTapInput, StringComparison.Ordinal);
        Assert.Contains("most specific semantic postcondition", postTapInput, StringComparison.Ordinal);
        Assert.Contains("same authoritative app state", postTapInput, StringComparison.Ordinal);
        Assert.DoesNotContain("CardSelectorView", postTapInput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_RetainsExactNamedTargetTapWithoutAppSpecificPrompting()
    {
        var storage = new InMemoryEncryptedStorage();
        var client = new FakeOpenAiClient(
        [
            CreateFunctionTurn(
                "query-secret-garden",
                "ansight_call_app_tool",
                new JsonObject
                {
                    ["toolId"] = "redpoint.mapbox.query_surface_contents",
                    ["arguments"] = new JsonObject { ["surfaceId"] = "1" }
                }),
            CreateFunctionTurn(
                "find-search-overlay",
                "ansight_find_ui",
                new JsonObject
                {
                    ["automationId"] = "map-dismiss-search-button",
                    ["visible"] = true
                }),
            CreateFunctionTurn(
                "tap-secret-garden",
                "ansight_tap_ui",
                new JsonObject
                {
                    ["type"] = "MapboxView",
                    ["visible"] = true,
                    ["enabled"] = true,
                    ["targetX"] = 516.97,
                    ["targetY"] = 721.75
                }),
            CreateFunctionTurn(
                "complete-secret-garden",
                "complete_instruction",
                new JsonObject
                {
                    ["outcome"] = "succeeded",
                    ["summary"] = "Secret Garden was selected."
                })
        ]);
        var gateway = new FakeToolGateway();
        gateway.Results.Enqueue(new ToolCallResult(
            false,
            "{\"isError\":false,\"result\":{\"payload\":{\"result\":{\"annotationManagers\":[{\"annotations\":[{\"label\":\"Secret Garden\",\"tapHint\":{\"tool\":\"ansight_tap_ui\",\"selector\":{\"type\":\"MapboxView\",\"visible\":true,\"enabled\":true},\"targetX\":516.97,\"targetY\":721.75}}]}]}}}}",
            "Queried map."));
        gateway.Results.Enqueue(new ToolCallResult(
            false,
            "{\"isError\":false,\"result\":{\"matches\":[],\"totalMatches\":0}}",
            "No overlay."));
        gateway.Results.Enqueue(new ToolCallResult(
            false,
            "{\"isError\":false,\"performed\":true}",
            "Tapped map."));
        using var service = new SimulatorAgentService(storage, client, gateway);
        service.SetDefaultModelAccessTokenForTesting("sk-local-test");

        var result = await service.RunAsync(new SimulatorAgentRunRequest(
            "session-123",
            ["Select Secret Garden on the map."]));

        Assert.Equal(SimulatorAgentRunStatus.Succeeded, result.Status);
        Assert.Contains("Host retained one exact named target", client.Inputs[1].ToJsonString());
        Assert.Contains("targetX", client.Inputs[1].ToJsonString());
        Assert.Contains("516.97", client.Inputs[1].ToJsonString());
        var postOverlayInput = client.Inputs[2].ToJsonString();
        Assert.Contains("targetX", postOverlayInput);
        Assert.Contains("516.97", postOverlayInput);
        Assert.Contains("targetY", postOverlayInput);
        Assert.Contains("721.75", postOverlayInput);
        Assert.DoesNotContain("search-overlay check", postOverlayInput);
        Assert.Equal(3, gateway.Calls.Count);
    }

    [Fact]
    public async Task RunAsync_DirectsActionFromVisibleTextToStableAncestorWithoutExplorationLoop()
    {
        var storage = new InMemoryEncryptedStorage();
        var client = new FakeOpenAiClient(
        [
            CreateFunctionTurn(
                "find-guide-enabled",
                "ansight_find_ui",
                new JsonObject
                {
                    ["text"] = "Open 3D Guide",
                    ["visible"] = true,
                    ["enabled"] = true
                }),
            CreateFunctionTurn(
                "find-guide-visible",
                "ansight_find_ui",
                new JsonObject
                {
                    ["text"] = "Open 3D Guide",
                    ["visible"] = true
                }),
            CreateFunctionTurn(
                "tap-guide",
                "ansight_tap_ui",
                new JsonObject
                {
                    ["automationId"] = "area-open3d-guide-button"
                }),
            CreateFunctionTurn(
                "complete-guide",
                "complete_instruction",
                new JsonObject
                {
                    ["outcome"] = "succeeded",
                    ["summary"] = "The 3D guide opened."
                })
        ]);
        var gateway = new FakeToolGateway();
        gateway.Results.Enqueue(new ToolCallResult(
            false,
            "{\"isError\":false,\"result\":{\"matches\":[],\"totalMatches\":0}}",
            "No matches."));
        gateway.Results.Enqueue(new ToolCallResult(
            false,
            "{\"isError\":false,\"result\":{\"matches\":[{\"text\":\"Open 3D Guide\",\"tapHint\":{\"tool\":\"ansight_tap_ui\",\"selector\":{\"automationId\":\"area-open3d-guide-button\"}}}],\"totalMatches\":1}}",
            "One match."));
        gateway.Results.Enqueue(new ToolCallResult(
            false,
            "{\"isError\":false,\"performed\":true}",
            "Tapped."));
        using var service = new SimulatorAgentService(storage, client, gateway);
        service.SetDefaultModelAccessTokenForTesting("sk-local-test");

        var result = await service.RunAsync(new SimulatorAgentRunRequest(
            "session-123",
            ["Select Open 3D Guide."]));

        Assert.Equal(SimulatorAgentRunStatus.Succeeded, result.Status);
        Assert.Contains("omit enabled", client.Inputs[1].ToJsonString(), StringComparison.Ordinal);
        Assert.Contains("call ansight_tap_ui now", client.Inputs[2].ToJsonString(), StringComparison.Ordinal);
        Assert.Contains("Do not enumerate controls", client.Inputs[2].ToJsonString(), StringComparison.Ordinal);
        Assert.Equal(3, gateway.Calls.Count);
    }

    [Fact]
    public async Task RunAsync_DoesNotTreatTypedSearchResultAsImplicitSelection()
    {
        var storage = new InMemoryEncryptedStorage();
        var client = new FakeOpenAiClient(
        [
            CreateFunctionTurn(
                "type-search",
                "ansight_type_text",
                new JsonObject
                {
                    ["automationId"] = "map-search-text-filter",
                    ["value"] = "Sikati Bay"
                }),
            CreateFunctionTurn(
                "find-search-result",
                "ansight_find_ui",
                new JsonObject
                {
                    ["text"] = "Sikati Bay",
                    ["visible"] = true
                }),
            CreateFunctionTurn(
                "complete-search",
                "complete_instruction",
                new JsonObject
                {
                    ["outcome"] = "succeeded",
                    ["summary"] = "The Sikati Bay search result is visible."
                })
        ]);
        var gateway = new FakeToolGateway();
        gateway.Results.Enqueue(new ToolCallResult(
            false,
            "{\"isError\":false,\"performed\":true}",
            "Typed."));
        gateway.Results.Enqueue(new ToolCallResult(
            false,
            "{\"isError\":false,\"result\":{\"matches\":[{\"text\":\"Sikati Bay\",\"tapHint\":{\"tool\":\"ansight_tap_ui\",\"selector\":{\"automationId\":\"search-result-sikati-bay\"}}}],\"totalMatches\":1}}",
            "One result."));
        using var service = new SimulatorAgentService(storage, client, gateway);
        service.SetDefaultModelAccessTokenForTesting("sk-local-test");

        var result = await service.RunAsync(new SimulatorAgentRunRequest(
            "session-123",
            ["When on the Home Screen, select the search bar and enter \"Sikati Bay\" then search."]));

        Assert.Equal(SimulatorAgentRunStatus.Succeeded, result.Status);
        Assert.Contains("Select a result only when", client.Inputs[1].ToJsonString());
        var postFindInput = client.Inputs[2].ToJsonString();
        Assert.Contains("verified the matching search result", postFindInput);
        Assert.Contains("do not follow its tapHint", postFindInput);
        Assert.DoesNotContain("call ansight_tap_ui now", postFindInput);
        Assert.Equal(2, gateway.Calls.Count);
    }

    [Fact]
    public async Task RunAsync_ChangesEvidenceSourceWhenTypedResultIsMissingFromSemanticTree()
    {
        var storage = new InMemoryEncryptedStorage();
        var client = new FakeOpenAiClient(
        [
            CreateFunctionTurn(
                "type-search",
                "ansight_type_text",
                new JsonObject
                {
                    ["automationId"] = "search-field",
                    ["value"] = "Example Place"
                }),
            CreateFunctionTurn(
                "find-search-result",
                "ansight_find_ui",
                new JsonObject
                {
                    ["text"] = "Example Place",
                    ["visible"] = true
                }),
            CreateFunctionTurn(
                "complete-search",
                "complete_instruction",
                new JsonObject
                {
                    ["outcome"] = "failed",
                    ["summary"] = "The semantic source could not establish an exact actionable result."
                })
        ]);
        var gateway = new FakeToolGateway();
        gateway.Results.Enqueue(new ToolCallResult(
            false,
            "{\"isError\":false,\"performed\":true}",
            "Typed."));
        gateway.Results.Enqueue(new ToolCallResult(
            false,
            "{\"isError\":false,\"result\":{\"matches\":[],\"totalMatches\":0}}",
            "No semantic match."));
        using var service = new SimulatorAgentService(storage, client, gateway);
        service.SetDefaultModelAccessTokenForTesting("sk-local-test");

        var result = await service.RunAsync(new SimulatorAgentRunRequest(
            "session-123",
            ["Search for and select Example Place."]));

        Assert.Equal(SimulatorAgentRunStatus.Failed, result.Status);
        var postFindInput = client.Inputs[2].ToJsonString();
        Assert.Contains("not represented by the current semantic tree", postFindInput);
        Assert.Contains("loading/searching state", postFindInput);
        Assert.Contains("focused result container", postFindInput);
        Assert.Contains("Do not tap an unverified list position", postFindInput);
        Assert.Equal(2, gateway.Calls.Count);
    }

    [Fact]
    public async Task RunAsync_AuditRedactsTypedTextAndCapturesExactToolResult()
    {
        var storage = new InMemoryEncryptedStorage();
        var client = new FakeOpenAiClient(
        [
            CreateFunctionTurn(
                "call-type",
                "ansight_type_text",
                new JsonObject
                {
                    ["value"] = "Secret Garden"
                }),
            CreateFunctionTurn(
                "call-complete",
                "complete_instruction",
                new JsonObject
                {
                    ["outcome"] = "succeeded",
                    ["summary"] = "Search text entered."
                })
        ]);
        var gateway = new FakeToolGateway
        {
            Result = new ToolCallResult(
                false,
                "{\"isError\":false,\"typedCharacterCount\":13}",
                "Typed 13 characters.")
        };
        using var service = new SimulatorAgentService(storage, client, gateway);
        service.SetDefaultModelAccessTokenForTesting("sk-local-test");

        var result = await service.RunAsync(new SimulatorAgentRunRequest(
            "session-123",
            ["Enter the search text."])
        {
            CaptureTrace = true
        });

        var toolAudit = Assert.Single(
            result.Audit.ToolCalls,
            static call => call.CallId == "call-type");
        Assert.DoesNotContain("Secret Garden", toolAudit.Arguments.Content, StringComparison.Ordinal);
        Assert.Contains("redacted 13 character", toolAudit.Arguments.Content, StringComparison.Ordinal);
        Assert.Equal("{\"isError\":false,\"typedCharacterCount\":13}", toolAudit.Result.Content);
        Assert.False(toolAudit.Result.WasTruncated);
    }

    [Fact]
    public async Task RunAsync_CompactsSupersededToolOutputsButKeepsAuditExact()
    {
        var storage = new InMemoryEncryptedStorage();
        var client = new FakeOpenAiClient(
        [
            CreateFunctionTurn(
                "call-observe",
                "ansight_get_live_visual_tree",
                new JsonObject()),
            CreateFunctionTurn(
                "call-assert",
                "ansight_assert_ui",
                new JsonObject { ["text"] = "Ready" }),
            CreateFunctionTurn(
                "call-complete",
                "complete_instruction",
                new JsonObject
                {
                    ["outcome"] = "succeeded",
                    ["summary"] = "Ready is visible."
                })
        ]);
        var largeObservation = new JsonObject
        {
            ["isError"] = false,
            ["result"] = new JsonObject
            {
                ["nodes"] = new string('x', 20_000)
            }
        }.ToJsonString();
        var assertion = "{\"isError\":false,\"message\":\"Ready is visible.\"}";
        var gateway = new FakeToolGateway();
        gateway.Results.Enqueue(new ToolCallResult(false, largeObservation, "Observed."));
        gateway.Results.Enqueue(new ToolCallResult(false, assertion, "Ready is visible."));
        using var service = new SimulatorAgentService(storage, client, gateway);
        service.SetDefaultModelAccessTokenForTesting("sk-local-test");

        var result = await service.RunAsync(new SimulatorAgentRunRequest(
            "session-123",
            ["Verify Ready is visible."])
        {
            CaptureTrace = true
        });

        Assert.Contains(new string('x', 1_000), client.Inputs[1].ToJsonString(), StringComparison.Ordinal);
        var finalInput = client.Inputs[2].ToJsonString();
        Assert.DoesNotContain(new string('x', 1_000), finalInput, StringComparison.Ordinal);
        var compactedFunctionOutput = Assert.Single(
            client.Inputs[2].OfType<JsonObject>(),
            item => item["type"]?.GetValue<string>() == "function_call_output"
                    && item["call_id"]?.GetValue<string>() == "call-observe");
        var compactedOutput = Assert.IsType<JsonObject>(JsonNode.Parse(
            compactedFunctionOutput["output"]?.GetValue<string>() ?? string.Empty));
        Assert.True(compactedOutput["superseded"]?.GetValue<bool>());
        Assert.Equal(largeObservation.Length, compactedOutput["originalCharacterCount"]?.GetValue<int>());
        Assert.Equal(largeObservation, result.Audit.ToolCalls[0].Result.Content);
    }

    [Fact]
    public async Task RunAsync_TrimsFunctionCallsAndOutputsAsPairs()
    {
        const int actionCount = 70;
        var turns = Enumerable.Range(0, actionCount)
            .Select(index => CreateFunctionTurn(
                $"call-tap-{index}",
                "ansight_tap_ui",
                new JsonObject
                {
                    ["screenX"] = 100 + index,
                    ["screenY"] = 200
                }))
            .Append(CreateFunctionTurn(
                "call-complete",
                "complete_instruction",
                new JsonObject
                {
                    ["outcome"] = "succeeded",
                    ["summary"] = "The repeated interaction sequence completed."
                }))
            .ToArray();
        var storage = new InMemoryEncryptedStorage();
        var client = new FakeOpenAiClient(turns);
        using var service = new SimulatorAgentService(storage, client, new FakeToolGateway());
        service.SetDefaultModelAccessTokenForTesting("sk-local-test");

        var result = await service.RunAsync(new SimulatorAgentRunRequest(
            "session-123",
            ["Perform the repeated interaction sequence."],
            MaximumTurnsPerInstruction: actionCount + 1)
        {
            MaximumRoundTrips = actionCount + 1
        });

        Assert.Equal(SimulatorAgentRunStatus.Succeeded, result.Status);
        Assert.DoesNotContain("call-tap-0", client.Inputs[^1].ToJsonString(), StringComparison.Ordinal);
        foreach (var input in client.Inputs)
        {
            var callIds = input.OfType<JsonObject>()
                .Where(item => item["type"]?.GetValue<string>() == "function_call")
                .Select(item => item["call_id"]?.GetValue<string>())
                .Where(static callId => callId is not null)
                .ToHashSet(StringComparer.Ordinal);
            var outputCallIds = input.OfType<JsonObject>()
                .Where(item => item["type"]?.GetValue<string>() == "function_call_output")
                .Select(item => item["call_id"]?.GetValue<string>())
                .Where(static callId => callId is not null);

            Assert.All(outputCallIds, callId => Assert.Contains(callId, callIds));
        }
    }

    [Fact]
    public async Task RunAsync_WithoutExecutionCredential_RejectsBeforeCallingOpenAi()
    {
        var storage = new InMemoryEncryptedStorage();
        using var client = new FakeOpenAiClient([]);
        using var service = new SimulatorAgentService(storage, client, new FakeToolGateway());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.RunAsync(
            new SimulatorAgentRunRequest("session-123", ["Tap Continue."])));

        Assert.Contains("account login", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(client.ApiKeys);
    }

    private static string CreateAppGraphCompletionSummary(params string[] destinationIds)
        => new JsonObject
        {
            ["schema"] = AppGraphExplorationSummaryValidator.ExplorationSchema,
            ["summary"] = "Exploration complete.",
            ["destinations"] = new JsonArray(destinationIds.Select(destinationId => (JsonNode?)new JsonObject
            {
                ["id"] = destinationId,
                ["kind"] = "screen",
                ["name"] = destinationId,
                ["parentScreen"] = null,
                ["synonyms"] = new JsonArray(),
                ["purpose"] = $"Use {destinationId}."
            }).ToArray()),
            ["navigationHosts"] = new JsonArray(),
            ["tabGroups"] = new JsonArray(),
            ["transitions"] = new JsonArray()
        }.ToJsonString();

    private static RepositoryTaskShortcut CreateRepositoryTaskShortcut(
        string toolName,
        string taskId,
        string title)
        => new(
            toolName,
            taskId,
            title,
            title,
            "test-feature",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject(),
                ["additionalProperties"] = false
            },
            100,
            1);

    private static FakeToolGateway CreateGatewayWithLifecycleTools()
    {
        var gateway = new FakeToolGateway();
        foreach (var toolName in new[]
                 {
                     "ansight_list_host_devices",
                     "ansight_start_device",
                     "ansight_launch_app",
                     "ansight_terminate_app",
                     "ansight_get_live_visual_tree"
                 })
        {
            gateway.ToolDefinitions.Add(new JsonObject
            {
                ["type"] = "function",
                ["name"] = toolName,
                ["description"] = toolName,
                ["parameters"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject(),
                    ["additionalProperties"] = false
                }
            });
        }

        return gateway;
    }

    private static bool IsLifecycleTool(string? toolName)
        => toolName is "ansight_list_host_devices"
            or "ansight_start_device"
            or "ansight_launch_app"
            or "ansight_terminate_app";

    private static OpenAiTurn CreateFunctionTurn(
        string callId,
        string name,
        JsonObject arguments)
    {
        var argumentText = arguments.ToJsonString();
        return new OpenAiTurn(
            $"resp-{callId}",
            "gpt-5.6-terra",
            new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "function_call",
                    ["call_id"] = callId,
                    ["name"] = name,
                    ["arguments"] = argumentText
                }
            },
            [new OpenAiFunctionCall(callId, name, arguments)],
            string.Empty,
            new SimulatorAgentTokenUsage(
                10,
                5,
                15,
                4,
                2,
                3));
    }

    private static ToolCallResult CreateCameraToolResult(double orientationY)
        => new(
            false,
            new JsonObject
            {
                ["isError"] = false,
                ["result"] = new JsonObject
                {
                    ["payload"] = new JsonObject
                    {
                        ["result"] = new JsonObject
                        {
                            ["player"] = new JsonObject
                            {
                                ["guide"] = new JsonObject
                                {
                                    ["cameraState"] = new JsonObject
                                    {
                                        ["position"] = new JsonObject { ["x"] = 1 },
                                        ["orientation"] = new JsonObject { ["y"] = orientationY }
                                    }
                                }
                            }
                        }
                    }
                }
            }.ToJsonString(),
            "Queried camera state.");

    private sealed class FakeOpenAiSessionFactory(
        IOpenAiSession session) : IOpenAiSessionFactory
    {
        public IOpenAiSession Create()
            => session;
    }

    private sealed class FakeOpenAiSession : IOpenAiSession
    {
        private readonly Queue<OpenAiTurn> responses;

        public FakeOpenAiSession(IEnumerable<OpenAiTurn> responses)
        {
            this.responses = new Queue<OpenAiTurn>(responses);
        }

        public List<OpenAiRequest> Requests { get; } = [];

        public List<JsonArray> IncrementalInputs { get; } = [];

        public List<OpenAiRequest> WarmupRequests { get; } = [];

        public bool IsDisposed { get; private set; }

        public Task WarmupAsync(OpenAiRequest request, CancellationToken cancellationToken)
        {
            WarmupRequests.Add(request);
            return Task.CompletedTask;
        }

        public Task<OpenAiTurn> CreateResponseAsync(
            OpenAiRequest request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request with
            {
                Input = request.Input.DeepClone().AsArray(),
                Tools = request.Tools.DeepClone().AsArray(),
                IncrementalInput = request.IncrementalInput?.DeepClone().AsArray()
            });
            IncrementalInputs.Add(request.IncrementalInput?.DeepClone().AsArray() ?? []);
            return Task.FromResult(responses.Dequeue());
        }

        public ValueTask DisposeAsync()
        {
            IsDisposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FailingOpenAiSession : IOpenAiSession
    {
        public Task<OpenAiTurn> CreateResponseAsync(
            OpenAiRequest request,
            CancellationToken cancellationToken)
            => throw new OpenAiWebSocketTransportException("Connection failed.");

        public ValueTask DisposeAsync()
            => ValueTask.CompletedTask;
    }

    private sealed class FakeOpenAiClient : IOpenAiClient
    {
        private readonly Queue<OpenAiTurn> responses;

        public FakeOpenAiClient(IEnumerable<OpenAiTurn> responses)
        {
            this.responses = new Queue<OpenAiTurn>(responses);
        }

        public List<string> ApiKeys { get; } = [];

        public List<OpenAiRequest> Requests { get; } = [];

        public List<JsonArray> Inputs { get; } = [];

        public List<JsonArray> Tools { get; } = [];




        public List<int> MaximumOutputTokens { get; } = [];

        public Task<OpenAiTurn> CreateResponseAsync(
            OpenAiRequest request,
            CancellationToken cancellationToken)
        {
            ApiKeys.Add(request.ApiKey);
            Requests.Add(request);
            Inputs.Add(request.Input.DeepClone().AsArray());
            Tools.Add(request.Tools.DeepClone().AsArray());
            MaximumOutputTokens.Add(request.MaximumOutputTokens);
            return Task.FromResult(responses.Dequeue());
        }

        public void Dispose()
        {
        }
    }

    private sealed record CapturedToolCall(
        string ToolName,
        JsonObject Arguments,
        string SessionId,
        string CorrelationId);

    private sealed class FakeToolGateway : IToolGateway
    {
        public List<CapturedToolCall> Calls { get; } = [];

        public List<bool> TaskTraceStates { get; } = [];

        public ToolCallResult Result { get; init; } = new(
            false,
            "{\"isError\":false}",
            "Completed.");

        public Queue<ToolCallResult> Results { get; } = new();

        public ToolCallResult? InitialObservation { get; init; }

        public Task<ToolCallResult?> CaptureInitialObservationAsync(
            string sessionId, SessionCapabilities capabilities, string correlationId, CancellationToken cancellationToken)
            => Task.FromResult(InitialObservation);

        public JsonArray ToolDefinitions { get; } = [];

        public IReadOnlyList<RepositoryTaskShortcut> RepositoryTaskShortcuts { get; init; } = [];

        public IReadOnlyList<SimulatorAgentRepositoryTaskDiscoveryTrace> RepositoryTaskDiscoveryTraces { get; init; } = [];

        public SessionCapabilities Capabilities { get; init; } =
            new(
                VisualTreeContract.IosRuntimePlatform,
                [VisualTreeContract.MauiToolId],
                []);

        public ToolSessionContext? ReplacementSession { get; init; }

        public List<string> ReboundSessionIds { get; } = [];

        public void BeginRun(
            string sessionId,
            SecretAccess secretAccess,
            string? targetDeviceIdentifier = null)
        {
        }

        public void EndRun()
        {
        }

        public void RebindRun(
            string sessionId,
            string? targetDeviceIdentifier = null)
        {
            ReboundSessionIds.Add(sessionId);
        }

        public JsonArray BuildOpenAiToolDefinitions(
            IReadOnlyList<RepositoryTaskShortcut>? repositoryTasks = null,
            SessionCapabilities? capabilities = null,
            bool appGraphEnabled = false)
        {
            var definitions = ToolDefinitions.DeepClone().AsArray();
            foreach (var task in repositoryTasks ?? [])
            {
                definitions.Add(new JsonObject
                {
                    ["type"] = "function",
                    ["name"] = task.ToolName,
                    ["description"] = task.Description,
                    ["parameters"] = task.InputSchema.DeepClone()
                });
            }

            return definitions;
        }

        public Task<IReadOnlyList<RepositoryTaskShortcut>> GetRepositoryTaskShortcutsAsync(
            string sessionId,
            string instruction,
            CancellationToken cancellationToken,
            Action<SimulatorAgentRepositoryTaskDiscoveryTrace>? trace = null)
        {
            foreach (var item in RepositoryTaskDiscoveryTraces)
            {
                trace?.Invoke(item);
            }
            return Task.FromResult(RepositoryTaskShortcuts);
        }

        public OpenAiFunctionCall NormalizeFunctionCall(
            OpenAiFunctionCall call)
        {
            var task = RepositoryTaskShortcuts.FirstOrDefault(candidate => string.Equals(
                candidate.ToolName,
                call.Name,
                StringComparison.Ordinal));
            return task is null
                ? call
                : new OpenAiFunctionCall(
                    call.CallId,
                    "ansight_run_task",
                    new JsonObject
                    {
                        ["taskId"] = task.TaskId,
                        ["input"] = call.Arguments.DeepClone()
                    });
        }

        public bool IsReadOnlyAppToolCall(JsonObject arguments, string sessionId)
            => true;

        public Task<ToolSessionContext?> GetSessionContextAsync(
            string sessionId,
            CancellationToken cancellationToken)
        {
            return Task.FromResult<ToolSessionContext?>(new ToolSessionContext(
                sessionId,
                "com.example.app",
                "Example App",
                IsLive: true,
                AppState: "foreground",
                Device: new SimulatorAgentRunDevice(
                    "test-device-123",
                    "Apple",
                    "iPhone 16 Pro",
                    "phone",
                    "iOS",
                    "18.6",
                    IsVirtual: true,
                    IsEmulator: true)));
        }

        public Task<SessionCapabilities> GetSessionCapabilitiesAsync(
            string sessionId,
            CancellationToken cancellationToken)
            => Task.FromResult(Capabilities);

        public Task<ToolSessionContext?> WaitForConnectedSessionAsync(
            string currentSessionId,
            string appId,
            string? targetDeviceIdentifier,
            TimeSpan timeout,
            CancellationToken cancellationToken)
            => Task.FromResult(ReplacementSession);

        public Task<ToolCallResult> ExecuteAsync(
            string toolName,
            JsonObject arguments,
            string sessionId,
            string correlationId,
            CancellationToken cancellationToken,
            Ansight.Host.Runtime.Operations.OperationExecutionContext? context = null)
        {
            Calls.Add(new CapturedToolCall(
                toolName,
                arguments.DeepClone().AsObject(),
                sessionId,
                correlationId));
            TaskTraceStates.Add(Ansight.Host.Runtime.Tasks.RepositoryTaskTraceScope.IsEnabled);
            return Task.FromResult(Results.Count > 0 ? Results.Dequeue() : Result);
        }
    }

    private sealed class SynchronousProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value)
        {
            report(value);
        }
    }

    private sealed class FakeAuditStore : IAuditStore
    {
        public SimulatorAgentRunAudit? SavedAudit { get; private set; }

        public AuditSaveResult Save(SimulatorAgentRunAudit audit)
        {
            SavedAudit = audit;
            return new AuditSaveResult("/tmp/simulator-agent-audit.json", null);
        }

        public IReadOnlyList<SimulatorAgentRunHistoryEntry> List()
            => SavedAudit is null
                ? Array.Empty<SimulatorAgentRunHistoryEntry>()
                : [new SimulatorAgentRunHistoryEntry(SavedAudit, "/tmp/simulator-agent-audit.json")];
    }
}
