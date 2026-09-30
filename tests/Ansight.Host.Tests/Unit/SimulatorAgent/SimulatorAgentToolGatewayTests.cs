using System.Text.Json.Nodes;
using Ansight.Host.Runtime.Operations;

namespace Ansight.Host.Tests.Unit.SimulatorAgent;

public sealed partial class SimulatorAgentToolGatewayTests
{
    [Fact]
    public async Task ExecuteAsync_PassesTrustedContextSeparatelyFromTaskArguments()
    {
        var dispatcher = new RecordingOperationDispatcher();
        var gateway = new ToolGateway(dispatcher);
        var context = new OperationExecutionContext("batch", "test", true);
        await gateway.ExecuteAsync("ansight_run_task", new JsonObject
        {
            ["taskId"] = "audio.test",
            ["input"] = new JsonObject { ["isParallelTestBatch"] = false }
        }, "session", "call", CancellationToken.None, context);

        Assert.Same(context, dispatcher.LastOperationContext);
        await gateway.ExecuteAsync("ansight_run_task", new JsonObject { ["taskId"] = "audio.test" },
            "session", "serial-call", CancellationToken.None);
        Assert.Null(dispatcher.LastOperationContext);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RepositoryTaskShortcuts_TraceExplainsFilteringWithoutChangingSelection(bool captureTrace)
    {
        const string instruction = "Open weather. Do not use weather-excluded.";
        var dispatcher = new RecordingOperationDispatcher();
        dispatcher.ResponseFactory = (_, arguments) =>
        {
            Assert.Equal(captureTrace, arguments?["includeDiagnostics"]?.GetValue<bool>() == true);
            return RequestResult.ToolResult(new JsonObject
            {
                ["tasks"] = new JsonArray(
                    Task("load-weather", 90, 0.9),
                    Task("weather-excluded", 90, 0.9),
                    Task("weather-low-score", 50, 0.8),
                    Task("weather-low-coverage", 80, 0.4),
                    Task("open-guide", 90, 0.9)),
                ["diagnostics"] = new JsonObject
                {
                    ["availableTaskCount"] = 7,
                    ["availableTaskIds"] = new JsonArray("load-weather", "weather-excluded", "weather-low-score", "weather-low-coverage", "open-guide", "hidden-task", "limited-task"),
                    ["candidates"] = new JsonArray(
                        Candidate("load-weather", 90, 0.9, "returned"),
                        Candidate("weather-excluded", 90, 0.9, "returned"),
                        Candidate("weather-low-score", 50, 0.8, "returned"),
                        Candidate("weather-low-coverage", 80, 0.4, "returned"),
                        Candidate("open-guide", 90, 0.9, "returned"),
                        Candidate("hidden-task", null, null, "no-match"),
                        Candidate("limited-task", 40, 0.6, "discovery-result-limit"))
                }
            }, isError: false);
        };
        var gateway = new ToolGateway(dispatcher);
        var traces = new List<SimulatorAgentRepositoryTaskDiscoveryTrace>();

        var shortcuts = await gateway.GetRepositoryTaskShortcutsAsync(
            "selected-session", instruction, CancellationToken.None, captureTrace ? traces.Add : null);

        Assert.Equal("load-weather", Assert.Single(shortcuts).TaskId);
        if (!captureTrace)
        {
            Assert.Empty(traces);
            return;
        }
        var first = traces[0];
        Assert.Equal(instruction, first.Query);
        Assert.Equal(7, first.AvailableTaskCount);
        Assert.Contains("hidden-task", first.AvailableTaskIds);
        Assert.Contains(first.Candidates, item => item.TaskId == "weather-excluded" && item.Reasons.Contains("explicit-exclusion"));
        Assert.Contains(first.Candidates, item => item.TaskId == "weather-low-score" && item.Reasons.Contains("below-minimum-score"));
        Assert.Contains(first.Candidates, item => item.TaskId == "weather-low-coverage" && item.Reasons.Contains("below-minimum-coverage"));
        Assert.Contains(first.Candidates, item => item.TaskId == "open-guide" && item.Reasons.Contains("task-intent-mismatch"));
        Assert.Contains(first.Candidates, item => item.TaskId == "hidden-task" && item.Score is null && item.Reasons.Contains("no-match"));
        Assert.Contains(first.Candidates, item => item.TaskId == "limited-task" && item.Reasons.Contains("discovery-result-limit"));
        Assert.Contains(traces.SelectMany(item => item.Candidates), item => item.Reasons.Contains("duplicate-earlier-match-retained"));
        Assert.Equal("selection", traces[^1].Stage);
        Assert.Equal(["load-weather"], traces[^1].SelectedTaskIds);

        static JsonObject Task(string taskId, double score, double coverage) => new()
        {
            ["taskId"] = taskId,
            ["title"] = taskId,
            ["description"] = taskId,
            ["feature"] = taskId == "open-guide" ? "guide" : "weather",
            ["inputSchema"] = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() },
            ["match"] = new JsonObject { ["score"] = score, ["coverage"] = coverage }
        };

        static JsonObject Candidate(string taskId, double? score, double? coverage, string reason) => new()
        {
            ["taskId"] = taskId,
            ["score"] = score,
            ["coverage"] = coverage,
            ["reason"] = reason
        };
    }

    [Fact]
    public async Task RepositoryTaskShortcuts_TraceReportsFinalPreloadCap()
    {
        var dispatcher = new RecordingOperationDispatcher();
        dispatcher.ResponseFactory = (_, arguments) => RequestResult.ToolResult(new JsonObject
        {
            ["tasks"] = new JsonArray((arguments?["query"]?.GetValue<string>() == "Download forecasts"
                ? Enumerable.Range(5, 2)
                : Enumerable.Range(1, 4)).Select(index => (JsonNode?)new JsonObject
            {
                ["taskId"] = $"weather-{index}",
                ["title"] = $"Weather {index}",
                ["description"] = "Open weather",
                ["feature"] = "weather",
                ["inputSchema"] = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() },
                ["match"] = new JsonObject { ["score"] = 90.0, ["coverage"] = 1.0 }
            }).ToArray())
        }, isError: false);
        var traces = new List<SimulatorAgentRepositoryTaskDiscoveryTrace>();

        var shortcuts = await new ToolGateway(dispatcher).GetRepositoryTaskShortcutsAsync(
            "selected-session", "Open weather. Download forecasts.", CancellationToken.None, traces.Add);

        Assert.Equal(5, shortcuts.Count);
        Assert.Equal("weather-6", Assert.Single(traces[^1].Candidates, item => item.Reasons.Contains("preload-task-limit")).TaskId);
        Assert.Contains("provider did not return inventory diagnostics", traces[0].Message);
    }

    [Fact]
    public void RequestContext_RecognizesDirectAndRepositoryTaskTestCalls()
    {
        var correlationId = RunRequestContext.CreateCorrelationId();
        var semanticOnlyCorrelationId = RunRequestContext.CreateCorrelationId(
            allowScreenshotOcr: false);

        Assert.True(RunRequestContext.IsTestRunCorrelationId(correlationId));
        Assert.True(RunRequestContext.IsTestRunCorrelationId($"{correlationId}:1"));
        Assert.True(RunRequestContext.IsTestRunCorrelationId(semanticOnlyCorrelationId));
        Assert.False(RunRequestContext.IsTestRunCorrelationId("repository-task-123"));
        Assert.True(RunRequestContext.AllowsScreenshotOcr(correlationId));
        Assert.False(RunRequestContext.AllowsScreenshotOcr(semanticOnlyCorrelationId));
        Assert.True(RunRequestContext.AllowsScreenshotOcr("repository-task-123"));
    }

    [Fact]
    public void BeginRun_EnablesHostManagedTestCaptureUntilRunEnds()
    {
        var dispatcher = new RecordingOperationDispatcher();
        var gateway = new ToolGateway(dispatcher);

        gateway.BeginRun("selected-session", SecretAccess.Empty);

        Assert.Equal("selected-session", dispatcher.ActiveSimulatorAgentSessionId);

        gateway.EndRun();

        Assert.Null(dispatcher.ActiveSimulatorAgentSessionId);
    }

    [Fact]
    public async Task ConcurrentRunsKeepSecretToolsIsolatedByExecutionContext()
    {
        var dispatcher = new RecordingOperationDispatcher(
            ToolDefinition(
                "ansight_type_text",
                "Type text",
                "sessionId",
                "value",
                "automationId"));
        var gateway = new ToolGateway(dispatcher);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var readyCount = 0;

        var aliases = await Task.WhenAll(
            Task.Run(() => ReadAliasAsync("session-one", "secret.one")),
            Task.Run(() => ReadAliasAsync("session-two", "secret.two")));

        Assert.Equal(["secret.one", "secret.two"], aliases.Order(StringComparer.Ordinal));

        async Task<string> ReadAliasAsync(string sessionId, string alias)
        {
            var metadata = new SimulatorAgentSecretMetadata(
                alias,
                "com.example.app",
                "version",
                DateTimeOffset.UtcNow);
            gateway.BeginRun(
                sessionId,
                new SecretAccess(
                    new Dictionary<string, SimulatorAgentSecretMetadata>(StringComparer.OrdinalIgnoreCase)
                    {
                        [alias] = metadata
                    },
                    requestedAlias => string.Equals(requestedAlias, alias, StringComparison.OrdinalIgnoreCase)
                        ? "value"
                        : null));
            try
            {
                if (Interlocked.Increment(ref readyCount) == 2)
                {
                    release.TrySetResult();
                }
                await release.Task.WaitAsync(TimeSpan.FromSeconds(2));

                var definitions = gateway.BuildOpenAiToolDefinitions();
                var secretTool = Assert.Single(definitions.OfType<JsonObject>(), definition =>
                    definition["name"]?.GetValue<string>() == "ansight_type_secret");
                var aliasValues = Assert.IsType<JsonArray>(
                    secretTool["parameters"]?["properties"]?["secretAlias"]?["enum"]);
                return Assert.Single(aliasValues.GetValues<string>());
            }
            finally
            {
                gateway.EndRun();
            }
        }
    }

    [Fact]
    public async Task WaitForConnectedSessionAsync_SelectsNewestLiveSessionForSameApp()
    {
        var dispatcher = new RecordingOperationDispatcher();
        var createdUtc = DateTimeOffset.UtcNow;
        dispatcher.SessionSnapshots.Add(CreateSessionSnapshot(
            "selected-session",
            "com.example.app",
            createdUtc));
        dispatcher.SessionSnapshots.Add(CreateSessionSnapshot(
            "replacement-session",
            "com.example.app",
            createdUtc.AddSeconds(1)));
        dispatcher.SessionSnapshots.Add(CreateSessionSnapshot(
            "other-app-session",
            "com.other.app",
            createdUtc.AddSeconds(2)));
        dispatcher.ConnectedSessionIds.Add("replacement-session");
        dispatcher.ConnectedSessionIds.Add("other-app-session");
        var gateway = new ToolGateway(dispatcher);

        var session = await gateway.WaitForConnectedSessionAsync(
            "selected-session",
            "com.example.app",
            targetDeviceIdentifier: null,
            TimeSpan.FromSeconds(1),
            CancellationToken.None);

        Assert.Equal("replacement-session", session?.SessionId);
        Assert.Equal("com.example.app", session?.AppId);
        Assert.True(session?.IsLive);
    }

    [Fact]
    public void BuildOpenAiToolDefinitions_ExposesLifecycleToolsWithoutHostControlledTargets()
    {
        var dispatcher = new RecordingOperationDispatcher(
            ToolDefinition(
                "ansight_launch_app",
                "Launch app",
                "sessionId",
                "appId",
                "deviceId",
                "bundleIdentifier"),
            ToolDefinition("not_allowed", "Not allowed", "value"));
        var gateway = new ToolGateway(dispatcher);

        var definitions = gateway.BuildOpenAiToolDefinitions();

        var definition = Assert.Single(definitions.OfType<JsonObject>());
        Assert.Equal("ansight_launch_app", definition["name"]?.GetValue<string>());
        var properties = Assert.IsType<JsonObject>(definition["parameters"]?["properties"]);
        Assert.DoesNotContain("sessionId", properties);
        Assert.DoesNotContain("appId", properties);
        Assert.DoesNotContain("deviceId", properties);
        Assert.DoesNotContain("bundleIdentifier", properties);
    }

    [Fact]
    public async Task TakeScreenshot_IsExposedAndBoundToTheSelectedSession()
    {
        var dispatcher = new RecordingOperationDispatcher(
            ToolDefinition(
                "ansight_take_screenshot",
                "Capture screenshot",
                "sessionId",
                "appId",
                "format",
                "quality",
                "maxWidth",
                "afterScreenUpdates"));
        dispatcher.Responses["ansight_take_screenshot"] = RequestResult.ToolResult(
            new JsonObject
            {
                ["captured"] = true
            },
            isError: false);
        var gateway = new ToolGateway(dispatcher);

        var definition = Assert.Single(gateway.BuildOpenAiToolDefinitions().OfType<JsonObject>());
        Assert.Equal("ansight_take_screenshot", definition["name"]?.GetValue<string>());
        var properties = Assert.IsType<JsonObject>(definition["parameters"]?["properties"]);
        Assert.DoesNotContain("sessionId", properties);
        Assert.DoesNotContain("appId", properties);
        Assert.Contains("format", properties);
        Assert.Contains("quality", properties);
        Assert.Contains("maxWidth", properties);
        Assert.Contains("afterScreenUpdates", properties);

        var result = await gateway.ExecuteAsync(
            "ansight_take_screenshot",
            new JsonObject
            {
                ["format"] = "jpeg",
                ["quality"] = 90,
                ["maxWidth"] = 1440,
                ["afterScreenUpdates"] = true
            },
            "selected-session",
            "correlation-id",
            CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal("selected-session", dispatcher.LastArguments?["sessionId"]?.GetValue<string>());
        Assert.Equal("jpeg", dispatcher.LastArguments?["format"]?.GetValue<string>());
        Assert.Equal(90, dispatcher.LastArguments?["quality"]?.GetValue<int>());
        Assert.Equal(1440, dispatcher.LastArguments?["maxWidth"]?.GetValue<int>());
        Assert.True(dispatcher.LastArguments?["afterScreenUpdates"]?.GetValue<bool>());
    }

    [Fact]
    public async Task RepositoryTaskShortcuts_ArePreloadedAsTypedToolsAndRunThroughCanonicalTaskTool()
    {
        var dispatcher = new RecordingOperationDispatcher(
            ToolDefinition("ansight_list_tasks", "List tasks", "sessionId", "query", "maxResults"),
            ToolDefinition("ansight_run_task", "Run task", "sessionId", "taskId", "input"));
        dispatcher.Responses["ansight_list_tasks"] = RequestResult.ToolResult(
            new JsonObject
            {
                ["tasks"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["taskId"] = "open-current-area-3d-guide",
                        ["title"] = "Open the current area 3D guide",
                        ["description"] = "Starts on the exact current AreaPage and opens its ready 3D guide.",
                        ["feature"] = "3d-guide",
                        ["inputSchema"] = new JsonObject
                        {
                            ["type"] = "object",
                            ["properties"] = new JsonObject
                            {
                                ["targetArea"] = new JsonObject { ["type"] = "string" }
                            },
                            ["required"] = new JsonArray("targetArea"),
                            ["additionalProperties"] = false
                        },
                        ["match"] = new JsonObject
                        {
                            ["score"] = 72.5,
                            ["coverage"] = 0.75
                        }
                    }
                }
            },
            isError: false);
        dispatcher.Responses["ansight_run_task"] = RequestResult.ToolResult(
            new JsonObject
            {
                ["taskId"] = "open-current-area-3d-guide",
                ["status"] = "Passed"
            },
            isError: false);
        var gateway = new ToolGateway(dispatcher);
        gateway.BeginRun("selected-session", SecretAccess.Empty);

        var shortcuts = await gateway.GetRepositoryTaskShortcutsAsync(
            "selected-session",
            "Open and verify the El Pati 3D guide",
            CancellationToken.None);
        var shortcut = Assert.Single(shortcuts);
        var definitions = gateway.BuildOpenAiToolDefinitions(shortcuts);
        var taskTool = Assert.Single(definitions.OfType<JsonObject>(), definition => string.Equals(
            definition["name"]?.GetValue<string>(),
            shortcut.ToolName,
            StringComparison.Ordinal));
        var taskProperties = Assert.IsType<JsonObject>(taskTool["parameters"]?["properties"]);
        Assert.Contains("targetArea", taskProperties);
        Assert.Contains("Repository task 'open-current-area-3d-guide'", taskTool["description"]?.GetValue<string>(), StringComparison.Ordinal);

        var normalizedCall = gateway.NormalizeFunctionCall(new OpenAiFunctionCall(
            "call-task",
            shortcut.ToolName,
            new JsonObject { ["targetArea"] = "El Pati" }));
        Assert.Equal("ansight_run_task", normalizedCall.Name);
        Assert.Equal("open-current-area-3d-guide", normalizedCall.Arguments["taskId"]?.GetValue<string>());
        Assert.Equal("El Pati", normalizedCall.Arguments["input"]?["targetArea"]?.GetValue<string>());

        var runResult = await gateway.ExecuteAsync(
            normalizedCall.Name,
            normalizedCall.Arguments,
            "selected-session",
            "correlation-task",
            CancellationToken.None);

        Assert.False(runResult.IsError);
        Assert.Equal("ansight_run_task", dispatcher.LastToolName);
        Assert.Equal("open-current-area-3d-guide", dispatcher.LastArguments?["taskId"]?.GetValue<string>());
        Assert.Equal("El Pati", dispatcher.LastArguments?["input"]?["targetArea"]?.GetValue<string>());
        Assert.Equal("selected-session", dispatcher.LastArguments?["sessionId"]?.GetValue<string>());
        gateway.EndRun();
    }

    [Fact]
    public async Task RepositoryTaskShortcuts_PreloadOnlyRelevantFocusedWorkflowSegments()
    {
        const string instruction =
            "Search for Siurana on the map and select it. Open its Details/AreaPage, use the AreaPage child search to find and select exactly El Pati, then open and verify the El Pati 3D guide. Use repository tasks for covered transitions. For the final transition, use open-current-area-3d-guide with targetArea El Pati.";
        var dispatcher = new RecordingOperationDispatcher(
            ToolDefinition("ansight_list_tasks", "List tasks", "sessionId", "query", "maxResults"));
        dispatcher.ResponseFactory = (toolName, arguments) =>
        {
            Assert.Equal("ansight_list_tasks", toolName);
            var query = arguments?["query"]?.GetValue<string>() ?? string.Empty;
            if (string.Equals(query, instruction, StringComparison.Ordinal))
            {
                Assert.Equal(5, arguments?["maxResults"]?.GetValue<int>());
            }
            JsonObject[] tasks = query switch
            {
                _ when string.Equals(query, instruction, StringComparison.Ordinal) =>
                [
                    RepositoryTaskMatch("open-selected-map-area-details", 71, 0.58),
                    RepositoryTaskMatch("focus-map-search-area", 66, 0.5),
                    RepositoryTaskMatch("fallback-one", 60, 0.48),
                    RepositoryTaskMatch("fallback-two", 59, 0.47),
                    RepositoryTaskMatch("fallback-three", 58, 0.46)
                ],
                _ when query.StartsWith("Search for Siurana", StringComparison.OrdinalIgnoreCase) =>
                [RepositoryTaskMatch("focus-map-search-area", 91, 0.88)],
                _ when query.StartsWith("Open its Details", StringComparison.OrdinalIgnoreCase) =>
                [RepositoryTaskMatch("open-selected-map-area-details", 94, 0.9)],
                _ when query.StartsWith("open and verify", StringComparison.OrdinalIgnoreCase) =>
                [
                    RepositoryTaskMatch("open-current-area-3d-guide", 96, 0.92)
                ],
                _ when query.StartsWith("For the final transition", StringComparison.OrdinalIgnoreCase) =>
                [
                    RepositoryTaskMatch("open-current-area-3d-guide", 98, 0.95)
                ],
                _ => []
            };
            return RequestResult.ToolResult(
                new JsonObject
                {
                    ["tasks"] = new JsonArray(tasks.Select(task => (JsonNode?)task).ToArray())
                },
                isError: false);
        };
        var gateway = new ToolGateway(dispatcher);

        var shortcuts = await gateway.GetRepositoryTaskShortcutsAsync(
            "selected-session",
            instruction,
            CancellationToken.None);

        Assert.Contains(shortcuts, task => task.TaskId == "focus-map-search-area");
        Assert.Contains(shortcuts, task => task.TaskId == "open-selected-map-area-details");
        Assert.Contains(shortcuts, task => task.TaskId == "open-current-area-3d-guide");
        Assert.Equal(3, shortcuts.Count);
        Assert.DoesNotContain(shortcuts, task => task.TaskId.StartsWith("fallback-", StringComparison.Ordinal));
        Assert.Equal(shortcuts.Count, shortcuts.Select(task => task.ToolName).Distinct(StringComparer.Ordinal).Count());
        Assert.Contains(
            gateway.BuildOpenAiToolDefinitions(shortcuts).OfType<JsonObject>(),
            tool => tool["name"]?.GetValue<string>() == "ansight_list_tasks");
        Assert.Contains(
            dispatcher.Calls,
            call => call.Arguments?["query"]?.GetValue<string>()
                .StartsWith("open and verify", StringComparison.OrdinalIgnoreCase) == true);

        static JsonObject RepositoryTaskMatch(string taskId, double score, double coverage)
            => new()
            {
                ["taskId"] = taskId,
                ["title"] = taskId,
                ["description"] = $"Description for {taskId}.",
                ["feature"] = "workflow",
                ["inputSchema"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject(),
                    ["additionalProperties"] = false
                },
                ["match"] = new JsonObject
                {
                    ["score"] = score,
                    ["coverage"] = coverage
                }
            };
    }

    [Fact]
    public async Task RepositoryTaskShortcuts_SearchesCommaSeparatedNavigationStepsIndependently()
    {
        const string instruction = "Search for Kalymnos, open a result, then return.";
        var dispatcher = new RecordingOperationDispatcher(
            ToolDefinition("ansight_list_tasks", "List tasks", "sessionId", "query", "maxResults"));
        dispatcher.ResponseFactory = (_, arguments) =>
        {
            var query = arguments?["query"]?.GetValue<string>();
            var tasks = new JsonArray();
            if (query == "Search for Kalymnos")
            {
                tasks.Add(new JsonObject
                {
                    ["taskId"] = "focus-map-search-area",
                    ["title"] = "Focus an exact map search area",
                    ["description"] = "Searches and selects the exact area.",
                    ["inputSchema"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject { ["searchArea"] = new JsonObject { ["type"] = "string" } },
                        ["required"] = new JsonArray("searchArea")
                    },
                    ["match"] = new JsonObject { ["score"] = 90.0, ["coverage"] = 0.8 }
                });
            }
            return RequestResult.ToolResult(new JsonObject { ["tasks"] = tasks }, isError: false);
        };
        var gateway = new ToolGateway(dispatcher);

        var tasks = await gateway.GetRepositoryTaskShortcutsAsync("selected-session", instruction, CancellationToken.None);

        Assert.Equal("focus-map-search-area", Assert.Single(tasks).TaskId);
        Assert.Contains(dispatcher.Calls, call => call.Arguments?["query"]?.GetValue<string>() == "open a result");
        Assert.Contains(dispatcher.Calls, call => call.Arguments?["query"]?.GetValue<string>() == "return");
    }

    [Fact]
    public async Task RepositoryTaskShortcuts_HonorOnlyExactExplicitTaskExclusions()
    {
        const string instruction =
            "Do not use open-area-3d-guide. Use open-current-area-3d-guide for El Pati.";
        var dispatcher = new RecordingOperationDispatcher(
            ToolDefinition("ansight_list_tasks", "List tasks", "sessionId", "query", "maxResults"));
        dispatcher.ResponseFactory = (_, _) => RequestResult.ToolResult(
            new JsonObject
            {
                ["tasks"] = new JsonArray(
                    RepositoryTaskMatch("open-area-3d-guide"),
                    RepositoryTaskMatch("open-current-area-3d-guide"))
            },
            isError: false);
        var gateway = new ToolGateway(dispatcher);

        var shortcuts = await gateway.GetRepositoryTaskShortcutsAsync(
            "selected-session",
            instruction,
            CancellationToken.None);

        var shortcut = Assert.Single(shortcuts);
        Assert.Equal("open-current-area-3d-guide", shortcut.TaskId);

        static JsonObject RepositoryTaskMatch(string taskId)
            => new()
            {
                ["taskId"] = taskId,
                ["title"] = taskId,
                ["description"] = taskId,
                ["feature"] = "3d-guide",
                ["inputSchema"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject(),
                    ["additionalProperties"] = false
                },
                ["match"] = new JsonObject
                {
                    ["score"] = 95d,
                    ["coverage"] = 0.9d
                }
            };
    }

    [Fact]
    public void BuildOpenAiToolDefinitions_DescribesBoundedPointTapCoordinates()
    {
        var dispatcher = new RecordingOperationDispatcher(
            ToolDefinition(
                "ansight_tap_ui",
                "Tap UI",
                "sessionId",
                "automationId",
                "screenX",
                "screenY",
                "targetX",
                "targetY"));
        var gateway = new ToolGateway(dispatcher);

        var definition = Assert.Single(gateway.BuildOpenAiToolDefinitions().OfType<JsonObject>());

        Assert.Contains("screenPosition", definition["description"]?.GetValue<string>(), StringComparison.Ordinal);
        var properties = Assert.IsType<JsonObject>(definition["parameters"]?["properties"]);
        Assert.DoesNotContain("sessionId", properties);
        Assert.Contains("automationId", properties);
        Assert.Contains("screenX", properties);
        Assert.Contains("screenY", properties);
        Assert.Contains("targetX", properties);
        Assert.Contains("targetY", properties);
    }

    [Fact]
    public void BuildOpenAiToolDefinitions_ExposesVisualTreeProviderForAccessibilityGaps()
    {
        var dispatcher = new RecordingOperationDispatcher(
            ToolDefinition(
                "ansight_get_live_visual_tree",
                "Get live tree",
                "sessionId",
                "toolId",
                "root",
                "includeProperties",
                "includeBindableProperties",
                "includeBindingContexts",
                "includeInactivePages",
                "includeComputedStyles",
                "includeProps",
                "includeState",
                "maxNodes"));
        var gateway = new ToolGateway(dispatcher);
        var capabilities = new SessionCapabilities(
            VisualTreeContract.IosRuntimePlatform,
            [VisualTreeContract.MauiToolId],
            []);

        var definition = Assert.Single(gateway.BuildOpenAiToolDefinitions(
            capabilities: capabilities).OfType<JsonObject>());

        var properties = Assert.IsType<JsonObject>(definition["parameters"]?["properties"]);
        Assert.Contains("toolId", properties);
        Assert.Contains("root", properties);
        Assert.DoesNotContain("includeBindableProperties", properties);
        Assert.DoesNotContain("includeComputedStyles", properties);
        Assert.DoesNotContain("includeProps", properties);
        Assert.DoesNotContain("includeState", properties);
        var providerIds = Assert.IsType<JsonArray>(properties["toolId"]?["enum"]);
        Assert.Equal([VisualTreeContract.MauiToolId], providerIds.GetValues<string>());
        Assert.Contains("iOS with .NET MAUI", definition["description"]?.GetValue<string>());
        Assert.DoesNotContain("React", definition["description"]?.GetValue<string>());
        Assert.DoesNotContain("Flutter", definition["description"]?.GetValue<string>());
        Assert.DoesNotContain("App Graph", definition["description"]?.GetValue<string>());
    }

    [Fact]
    public void BuildOpenAiToolDefinitions_ExposesFrameworkNavigationStructureWithoutHostTarget()
    {
        var dispatcher = new RecordingOperationDispatcher(
            ToolDefinition(
                "ansight_get_live_navigation_structure",
                "Get navigation structure",
                "sessionId",
                "appId",
                "framework"));
        var gateway = new ToolGateway(dispatcher);
        var capabilities = new SessionCapabilities(
            VisualTreeContract.IosRuntimePlatform,
            [],
            ["ios-uikit"]);

        var definition = Assert.Single(gateway.BuildOpenAiToolDefinitions(
            capabilities: capabilities).OfType<JsonObject>());

        var properties = Assert.IsType<JsonObject>(definition["parameters"]?["properties"]);
        Assert.DoesNotContain("sessionId", properties);
        Assert.DoesNotContain("appId", properties);
        Assert.Contains("framework", properties);
        var frameworks = Assert.IsType<JsonArray>(properties["framework"]?["enum"]);
        Assert.Equal(["ios-uikit"], frameworks.GetValues<string>());
        Assert.Contains("ios-uikit", definition["description"]?.GetValue<string>());
        Assert.DoesNotContain("React Native", definition["description"]?.GetValue<string>());
    }

    [Fact]
    public void BuildOpenAiToolDefinitions_OmitsNavigationWithoutPublishedController()
    {
        var dispatcher = new RecordingOperationDispatcher(
            ToolDefinition(
                "ansight_get_live_navigation_structure",
                "Get navigation structure",
                "framework"));
        var gateway = new ToolGateway(dispatcher);

        var definitions = gateway.BuildOpenAiToolDefinitions(
            capabilities: SessionCapabilities.Empty("iOS"));

        Assert.Empty(definitions);
    }

    [Fact]
    public void ContextSnapshot_MauiIos_ContainsOnlyPublishedTechnology()
    {
        var capabilities = BuildCapabilities(
            "iOS",
            VisualTreeContract.MauiToolId,
            "maui.get_navigation_state");

        var snapshot = BuildContextSnapshot(capabilities);

        Assert.Equal(
            """
            profile=iOS with .NET MAUI
            visual.providers=maui.get_visual_tree
            visual.parameters=includeBounds,maxDepth,maxNodes,root,toolId
            navigation.frameworks=maui
            navigation.parameters=framework
            context.technologies=.NET MAUI
            context.appGraph=False
            """,
            snapshot);
    }

    [Fact]
    public void ContextSnapshot_ReactNative_ContainsOnlyPublishedTechnology()
    {
        var capabilities = BuildCapabilities(
            "iOS",
            VisualTreeContract.ReactShadowToolId,
            VisualTreeContract.ReactComponentToolId,
            "react.get_navigation_state");

        var snapshot = BuildContextSnapshot(capabilities);

        Assert.Equal(
            """
            profile=iOS with React Native
            visual.providers=react.get_shadow_tree,react.get_component_tree
            visual.parameters=includeBounds,includeProps,includeState,maxDepth,maxNodes,toolId
            navigation.frameworks=react-native
            navigation.parameters=framework
            context.technologies=React Native
            context.appGraph=False
            """,
            snapshot);
    }

    [Fact]
    public void ContextSnapshot_FlutterAndroid_ContainsOnlyPublishedTechnology()
    {
        var capabilities = BuildCapabilities(
            "Android",
            VisualTreeContract.FlutterToolId,
            "flutter.get_navigation_state");

        var snapshot = BuildContextSnapshot(capabilities);

        Assert.Equal(
            """
            profile=Android with Flutter
            visual.providers=flutter.get_widget_tree
            visual.parameters=includeBounds,maxDepth,maxNodes,toolId
            navigation.frameworks=flutter
            navigation.parameters=framework
            context.technologies=Flutter
            context.appGraph=False
            """,
            snapshot);
    }

    [Fact]
    public void ContextSnapshot_DomWeb_OmitsNavigationAndOtherTechnologies()
    {
        var capabilities = BuildCapabilities(
            "web",
            VisualTreeContract.DomToolId);

        var snapshot = BuildContextSnapshot(capabilities);

        Assert.Equal(
            """
            profile=web with DOM
            visual.providers=dom.get_document
            visual.parameters=includeBounds,maxDepth,maxNodes,toolId
            navigation.frameworks=
            navigation.parameters=
            context.technologies=DOM
            context.appGraph=False
            """,
            snapshot);
    }

    [Fact]
    public void ContextSnapshot_MixedToolkit_ContainsUnionOfPublishedCapabilities()
    {
        var capabilities = BuildCapabilities(
            "iOS",
            VisualTreeContract.MauiToolId,
            VisualTreeContract.ReactShadowToolId,
            VisualTreeContract.NativeToolId,
            "maui.get_navigation_state",
            "react.get_navigation_state",
            "ios.uikit.get_navigation_state");

        var snapshot = BuildContextSnapshot(capabilities);

        Assert.Equal(
            """
            profile=iOS with .NET MAUI and React Native
            visual.providers=maui.get_visual_tree,react.get_shadow_tree,ui.get_visual_tree
            visual.parameters=includeBounds,includeComputedStyles,includeProps,maxDepth,maxNodes,root,toolId
            navigation.frameworks=maui,react-native,ios-uikit
            navigation.parameters=framework
            context.technologies=.NET MAUI,React Native
            context.appGraph=False
            """,
            snapshot);
    }

    [Fact]
    public async Task GetSessionCapabilitiesAsync_UsesRuntimePlatformAndExecutablePublishedTools()
    {
        var capturedAtUtc = DateTimeOffset.UtcNow;
        var dispatcher = new RecordingOperationDispatcher
        {
            AppToolCatalog = new JsonObject
            {
                ["tools"] = new JsonArray(
                    AppTool(VisualTreeContract.MauiToolId, "read", executable: true),
                    AppTool(VisualTreeContract.ReactShadowToolId, "read", executable: false),
                    AppTool("ios.uikit.get_navigation_state", "read", executable: true))
            }
        };
        dispatcher.SessionSnapshots.Add(CreateSessionSnapshot(
            "selected-session",
            "com.example.app",
            capturedAtUtc,
            [
                new SessionVisualTreeSnapshot
                {
                    SnapshotId = "tree-1",
                    CapturedAtUtc = capturedAtUtc,
                    RuntimePlatform = "iOS",
                    Source = "sdk.touchCapture",
                    NodeCount = 1
                }
            ]));
        var gateway = new ToolGateway(dispatcher);

        var capabilities = await gateway.GetSessionCapabilitiesAsync(
            "selected-session",
            CancellationToken.None);

        Assert.Equal(VisualTreeContract.IosRuntimePlatform, capabilities.RuntimePlatform);
        Assert.Equal([VisualTreeContract.MauiToolId], capabilities.VisualTreeToolIds);
        Assert.Equal(["ios-uikit"], capabilities.NavigationFrameworks);
    }

    [Fact]
    public void BuildOpenAiToolDefinitions_ReturnsAllFocusedFindCandidates()
    {
        var dispatcher = new RecordingOperationDispatcher(
            ToolDefinition(
                "ansight_find_ui",
                "Find UI",
                "sessionId",
                "automationId",
                "role",
                "matchMode",
                "index"));
        var gateway = new ToolGateway(dispatcher);

        var definition = Assert.Single(gateway.BuildOpenAiToolDefinitions().OfType<JsonObject>());

        var properties = Assert.IsType<JsonObject>(definition["parameters"]?["properties"]);
        Assert.Contains("automationId", properties);
        Assert.Contains("role", properties);
        Assert.Contains("matchMode", properties);
        Assert.DoesNotContain("index", properties);
        Assert.Contains("Do not add role", definition["description"]?.GetValue<string>());
        Assert.Contains("matchScore", definition["description"]?.GetValue<string>());
    }

    [Fact]
    public async Task ExecuteAsync_EnforcesSelectedSessionForLifecycleTool()
    {
        var dispatcher = new RecordingOperationDispatcher();
        var gateway = new ToolGateway(dispatcher);

        var result = await gateway.ExecuteAsync(
            "ansight_launch_app",
            new JsonObject
            {
                ["sessionId"] = "another-session",
                ["appId"] = "another.app",
                ["deviceId"] = "another-device",
                ["bundleIdentifier"] = "another.bundle"
            },
            "selected-session",
            "correlation-1",
            CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal("ansight_launch_app", dispatcher.LastToolName);
        Assert.Equal("selected-session", dispatcher.LastArguments?["sessionId"]?.GetValue<string>());
        Assert.False(dispatcher.LastArguments?.ContainsKey("appId"));
        Assert.False(dispatcher.LastArguments?.ContainsKey("deviceId"));
        Assert.False(dispatcher.LastArguments?.ContainsKey("bundleIdentifier"));
    }

    [Fact]
    public async Task ExecuteAsync_AppliesCompactObservationAndWaitLimits()
    {
        var dispatcher = new RecordingOperationDispatcher();
        var gateway = new ToolGateway(dispatcher);

        await gateway.ExecuteAsync(
            "ansight_get_live_visual_tree",
            new JsonObject
            {
                ["toolId"] = "maui.get_visual_tree",
                ["maxNodes"] = 5_000,
                ["maxDepth"] = 100,
                ["includeProperties"] = true
            },
            "selected-session",
            "correlation-observe",
            CancellationToken.None);
        var observationArguments = Assert.IsType<JsonObject>(dispatcher.LastArguments);
        Assert.Equal(400, observationArguments["maxNodes"]?.GetValue<int>());
        Assert.Equal(32, observationArguments["maxDepth"]?.GetValue<int>());
        Assert.False(observationArguments["includeProperties"]?.GetValue<bool>());
        Assert.Equal("maui.get_visual_tree", observationArguments["toolId"]?.GetValue<string>());

        await gateway.ExecuteAsync(
            "ansight_wait_for_ui",
            new JsonObject { ["timeoutMs"] = 30_000 },
            "selected-session",
            "correlation-wait",
            CancellationToken.None);
        var waitArguments = Assert.IsType<JsonObject>(dispatcher.LastArguments);
        Assert.Equal(8_000, waitArguments["timeoutMs"]?.GetValue<int>());
    }

    [Fact]
    public async Task ExecuteAsync_PreservesExactDeviceAccessibilitySnapshotAsTraceEvidence()
    {
        var dispatcher = new RecordingOperationDispatcher();
        dispatcher.Responses["ansight_get_live_visual_tree"] = RequestResult.ToolResult(
            new JsonObject
            {
                ["sessionId"] = "selected-session",
                ["appId"] = "com.example.app",
                ["toolId"] = "device.accessibility",
                ["payload"] = new JsonObject
                {
                    ["result"] = new JsonObject
                    {
                        ["format"] = "ansight.device-accessibility.compact.v2",
                        ["capturedAtUtc"] = "2026-08-27T04:00:00Z",
                        ["coordinateSpace"] = new JsonObject
                        {
                            ["x"] = 0,
                            ["y"] = 0,
                            ["width"] = 1,
                            ["height"] = 1
                        },
                        ["types"] = new JsonArray("Application", "Button"),
                        ["root"] = new JsonObject
                        {
                            ["typeId"] = 0,
                            ["role"] = "application",
                            ["flags"] = 3,
                            ["bounds"] = new JsonObject
                            {
                                ["x"] = 0,
                                ["y"] = 0,
                                ["width"] = 1,
                                ["height"] = 1
                            },
                            ["children"] = new JsonArray
                            {
                                new JsonObject
                                {
                                    ["typeId"] = 1,
                                    ["role"] = "button",
                                    ["automationId"] = "home-tab-crags",
                                    ["text"] = "Crags",
                                    ["flags"] = 3,
                                    ["bounds"] = new JsonObject
                                    {
                                        ["x"] = 0.25,
                                        ["y"] = 0.9,
                                        ["width"] = 0.25,
                                        ["height"] = 0.1
                                    },
                                    ["children"] = new JsonArray()
                                }
                            }
                        },
                        ["nodeCount"] = 2,
                        ["truncated"] = false
                    }
                },
                ["persistedVisualTree"] = new JsonObject
                {
                    ["snapshotId"] = "snapshot-1"
                }
            },
            isError: false);
        var gateway = new ToolGateway(dispatcher);

        var result = await gateway.ExecuteAsync(
            "ansight_get_live_visual_tree",
            new JsonObject(),
            "selected-session",
            "correlation-accessibility",
            CancellationToken.None);

        Assert.False(result.IsError);
        var evidence = Assert.IsType<JsonObject>(result.AccessibilityEvidence);
        Assert.Equal("device.accessibility", evidence["source"]?.GetValue<string>());
        Assert.Equal(2, evidence["nodeCount"]?.GetValue<int>());
        Assert.Equal("snapshot-1", evidence["snapshotId"]?.GetValue<string>());
        Assert.Equal(
            "home-tab-crags",
            evidence["snapshot"]?["root"]?["children"]?[0]?["automationId"]?.GetValue<string>());
        Assert.Contains("home-tab-crags", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteAsync_IncludesBoundedAfterActionObservationForNewDialog()
    {
        var capturedAtUtc = DateTimeOffset.Parse("2026-08-31T07:23:43.449174Z");
        var afterPayload = new JsonObject
        {
            ["format"] = "ansight.device-accessibility.compact.v2",
            ["platform"] = "ios",
            ["capturedAtUtc"] = capturedAtUtc,
            ["coordinateSpace"] = new JsonObject
            {
                ["x"] = 0,
                ["y"] = 0,
                ["width"] = 1,
                ["height"] = 1
            },
            ["flagBits"] = new JsonObject
            {
                ["visible"] = 1,
                ["enabled"] = 2
            },
            ["types"] = new JsonArray("Application", "Group", "StaticText", "TextArea"),
            ["root"] = new JsonObject
            {
                ["typeId"] = 0,
                ["role"] = "application",
                ["flags"] = 3,
                ["bounds"] = new JsonObject
                {
                    ["x"] = 0,
                    ["y"] = 0,
                    ["width"] = 1,
                    ["height"] = 1
                },
                ["children"] = new JsonArray(
                    new JsonObject
                    {
                        ["typeId"] = 1,
                        ["role"] = "view",
                        ["automationId"] = "PopoverDismissRegion",
                        ["text"] = "dismiss popup",
                        ["flags"] = 3,
                        ["bounds"] = new JsonObject
                        {
                            ["x"] = 0,
                            ["y"] = 0,
                            ["width"] = 1,
                            ["height"] = 1
                        },
                        ["children"] = new JsonArray()
                    },
                    new JsonObject
                    {
                        ["typeId"] = 2,
                        ["role"] = "text",
                        ["text"] = "General Feedback",
                        ["flags"] = 3,
                        ["children"] = new JsonArray()
                    },
                    new JsonObject
                    {
                        ["typeId"] = 3,
                        ["role"] = "textbox",
                        ["automationId"] = "feedback-description-entry",
                        ["flags"] = 3,
                        ["children"] = new JsonArray()
                    })
            },
            ["nodeCount"] = 4,
            ["truncated"] = false
        };
        var afterSnapshot = new SessionVisualTreeSnapshot
        {
            SnapshotId = "action-after-dialog",
            CapturedAtUtc = capturedAtUtc,
            VisualTreeKind = "accessibility",
            VisualTreeFormat = "ansight.device-accessibility.compact.v2",
            RuntimePlatform = "ios",
            Source = "core-simulator-ax-service",
            NodeCount = 4,
            ActionId = "action-dialog",
            ActionCapability = "ui.tap",
            EvidencePhase = "after",
            Payload = afterPayload
        };
        var dispatcher = new RecordingOperationDispatcher();
        dispatcher.SessionSnapshots.Add(CreateSessionSnapshot(
            "selected-session",
            "com.example.app",
            capturedAtUtc,
            [afterSnapshot]));
        dispatcher.Responses["ansight_tap_ui"] = RequestResult.ToolResult(
            new JsonObject
            {
                ["performed"] = true,
                ["message"] = "Tapped.",
                ["evidence"] = new JsonObject
                {
                    ["after"] = new JsonObject
                    {
                        ["visualTreeSnapshotId"] = afterSnapshot.SnapshotId
                    }
                }
            },
            isError: false);
        var gateway = new ToolGateway(dispatcher);

        var result = await gateway.ExecuteAsync(
            "ansight_tap_ui",
            new JsonObject { ["automationId"] = "feedback-button" },
            "selected-session",
            "correlation-action-dialog",
            CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Contains("afterObservation", result.Output, StringComparison.Ordinal);
        Assert.Contains("General Feedback", result.Output, StringComparison.Ordinal);
        Assert.Contains("feedback-description-entry", result.Output, StringComparison.Ordinal);
        Assert.Equal(
            afterSnapshot.SnapshotId,
            result.AccessibilityEvidence?["snapshotId"]?.GetValue<string>());
    }

    [Fact]
    public async Task ExecuteAsync_BoundsOversizedModelResultsAndPreservesAuditEvidence()
    {
        var dispatcher = new RecordingOperationDispatcher();
        dispatcher.Responses["ansight_assert_ui"] = RequestResult.ToolResult(
            new JsonObject
            {
                ["message"] = "Large result.",
                ["content"] = new string('x', 20_000)
            },
            isError: false);
        var gateway = new ToolGateway(dispatcher);

        var result = await gateway.ExecuteAsync(
            "ansight_assert_ui",
            new JsonObject { ["text"] = "Ready" },
            "selected-session",
            "correlation-large",
            CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Contains(new string('x', 20_000), result.Output, StringComparison.Ordinal);
        Assert.NotNull(result.ModelOutput);
        Assert.True(result.ModelOutput.Length < 6_000);
        Assert.Contains("\"truncated\":true", result.ModelOutput, StringComparison.Ordinal);
        Assert.Contains("\"originalCharacterCount\"", result.ModelOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteAsync_RemovesOcrTraceEvidenceFromModelOutputAndRetainsItForAudit()
    {
        var traceEvidence = new JsonObject
        {
            ["provider"] = "tesseract",
            ["screenshotArtifactPath"] = "/private/session/ocr-source.png",
            ["detectionCount"] = 1,
            ["detections"] = new JsonArray(
                new JsonObject { ["text"] = "Account" })
        };
        var dispatcher = new RecordingOperationDispatcher();
        dispatcher.Responses["ansight_scan_screen"] = RequestResult.ToolResult(
            new JsonObject
            {
                ["message"] = "Scanned visible content.",
                ["content"] = new JsonArray(
                    new JsonObject { ["text"] = "Account" }),
                [LiveUiOcrTraceEvidence.PayloadPropertyName] = traceEvidence
            },
            isError: false);
        var gateway = new ToolGateway(dispatcher);

        var result = await gateway.ExecuteAsync(
            "ansight_scan_screen",
            new JsonObject(),
            "selected-session",
            "correlation-ocr",
            CancellationToken.None);

        Assert.False(result.IsError);
        Assert.NotNull(result.TraceEvidence);
        Assert.Equal("tesseract", result.TraceEvidence?["provider"]?.GetValue<string>());
        Assert.Contains("Account", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain(LiveUiOcrTraceEvidence.PayloadPropertyName, result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("ocr-source.png", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TypeSecret_ExposesOnlyAliasesAndReturnsARedactedResult()
    {
        var dispatcher = new RecordingOperationDispatcher(ToolDefinition(
            "ansight_type_text",
            "Type text",
            "sessionId",
            "automationId",
            "value"));
        var metadata = new SimulatorAgentSecretMetadata(
            "login.password",
            "com.example.app",
            "version-1",
            DateTimeOffset.UtcNow);
        var gateway = new ToolGateway(dispatcher);
        gateway.BeginRun(
            "selected-session",
            new SecretAccess(
                new Dictionary<string, SimulatorAgentSecretMetadata>(StringComparer.OrdinalIgnoreCase)
                {
                    [metadata.Alias] = metadata
                },
                alias => string.Equals(alias, metadata.Alias, StringComparison.OrdinalIgnoreCase)
                    ? "sensitive-value"
                    : null));

        var definition = Assert.Single(
            gateway.BuildOpenAiToolDefinitions().OfType<JsonObject>(),
            item => item["name"]?.GetValue<string>() == "ansight_type_secret");
        var properties = Assert.IsType<JsonObject>(definition["parameters"]?["properties"]);
        Assert.Contains("secretAlias", properties);
        Assert.DoesNotContain("value", properties);

        var result = await gateway.ExecuteAsync(
            "ansight_type_secret",
            new JsonObject
            {
                ["secretAlias"] = "login.password",
                ["automationId"] = "password-field"
            },
            "selected-session",
            "correlation-secret",
            CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal("ansight_type_text", dispatcher.LastToolName);
        Assert.Equal("sensitive-value", dispatcher.LastArguments?["value"]?.GetValue<string>());
        Assert.Equal("selected-session", dispatcher.LastArguments?["sessionId"]?.GetValue<string>());
        Assert.DoesNotContain("sensitive-value", result.Output, StringComparison.Ordinal);
        Assert.Contains("login.password", result.Output, StringComparison.Ordinal);

        gateway.EndRun();
        Assert.DoesNotContain(
            gateway.BuildOpenAiToolDefinitions().OfType<JsonObject>(),
            item => item["name"]?.GetValue<string>() == "ansight_type_secret");
    }

    [Fact]
    public async Task AppToolBridge_RequiresDiscoveryAndAllowsReadAndWriteToolsButRejectsCriticalTools()
    {
        var dispatcher = new RecordingOperationDispatcher();
        dispatcher.Responses["ansight_list_app_tools"] = RequestResult.ToolResult(
            new JsonObject
            {
                ["sessionId"] = "selected-session",
                ["appId"] = "com.example.app",
                ["catalog"] = new JsonObject
                {
                    ["tools"] = new JsonArray
                    {
                        AppTool(
                            "example.validate_state",
                            "read",
                            executable: true),
                        AppTool(
                            "example.select_state",
                            "write",
                            executable: true),
                        AppTool(
                            "example.clear_cache",
                            "critical",
                            executable: true),
                        AppTool(
                            "example.read_secret",
                            "critical",
                            executable: true)
                    }
                }
            },
            isError: false);
        var gateway = new ToolGateway(dispatcher);
        gateway.BeginRun("selected-session", SecretAccess.Empty);

        var undiscoveredCall = await gateway.ExecuteAsync(
            "ansight_call_app_tool",
            new JsonObject { ["toolId"] = "example.validate_state" },
            "selected-session",
            "correlation-undiscovered",
            CancellationToken.None);
        var discovery = await gateway.ExecuteAsync(
            "ansight_list_app_tools",
            new JsonObject { ["feature"] = "state validation" },
            "selected-session",
            "correlation-discovery",
            CancellationToken.None);
        var cachedDiscovery = await gateway.ExecuteAsync(
            "ansight_list_app_tools",
            new JsonObject { ["feature"] = "STATE VALIDATION" },
            "selected-session",
            "correlation-cached-discovery",
            CancellationToken.None);
        var allowedCall = await gateway.ExecuteAsync(
            "ansight_call_app_tool",
            new JsonObject
            {
                ["toolId"] = "example.validate_state",
                ["arguments"] = new JsonObject { ["expected"] = "ready" }
            },
            "selected-session",
            "correlation-allowed",
            CancellationToken.None);
        var runtimeWriteCall = await gateway.ExecuteAsync(
            "ansight_call_app_tool",
            new JsonObject { ["toolId"] = "example.select_state" },
            "selected-session",
            "correlation-runtime-write",
            CancellationToken.None);
        var appDataWriteCall = await gateway.ExecuteAsync(
            "ansight_call_app_tool",
            new JsonObject { ["toolId"] = "example.clear_cache" },
            "selected-session",
            "correlation-app-data-write",
            CancellationToken.None);
        var criticalCall = await gateway.ExecuteAsync(
            "ansight_call_app_tool",
            new JsonObject { ["toolId"] = "example.read_secret" },
            "selected-session",
            "correlation-critical",
            CancellationToken.None);

        Assert.True(undiscoveredCall.IsError);
        Assert.Contains("ansight_list_app_tools", undiscoveredCall.Message, StringComparison.Ordinal);
        Assert.False(discovery.IsError);
        Assert.Contains("example.validate_state", discovery.Output, StringComparison.Ordinal);
        Assert.Contains("example.select_state", discovery.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("example.clear_cache", discovery.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("example.read_secret", discovery.Output, StringComparison.Ordinal);
        Assert.Contains("Reused cached discovery", cachedDiscovery.Message, StringComparison.Ordinal);
        Assert.False(allowedCall.IsError);
        Assert.False(runtimeWriteCall.IsError);
        Assert.True(gateway.IsReadOnlyAppToolCall(
            new JsonObject { ["toolId"] = "example.validate_state" },
            "selected-session"));
        Assert.False(gateway.IsReadOnlyAppToolCall(
            new JsonObject { ["toolId"] = "example.select_state" },
            "selected-session"));
        Assert.True(appDataWriteCall.IsError);
        Assert.True(criticalCall.IsError);
        Assert.Equal(3, dispatcher.Calls.Count);
        Assert.Equal("ansight_list_app_tools", dispatcher.Calls[0].ToolName);
        Assert.Equal("ansight_call_app_tool", dispatcher.Calls[1].ToolName);
        Assert.Equal("selected-session", dispatcher.Calls[1].Arguments?["sessionId"]?.GetValue<string>());
        Assert.Equal("ansight_call_app_tool", dispatcher.Calls[2].ToolName);
        Assert.Equal("example.select_state", dispatcher.Calls[2].Arguments?["toolId"]?.GetValue<string>());
    }

    [Fact]
    public async Task AppToolBridge_CachesReadAndWriteDiscoverySeparately()
    {
        var dispatcher = new RecordingOperationDispatcher();
        var gateway = new ToolGateway(dispatcher);
        gateway.BeginRun("selected-session", SecretAccess.Empty);

        await gateway.ExecuteAsync(
            "ansight_list_app_tools",
            new JsonObject { ["query"] = "map control", ["policy"] = "read" },
            "selected-session",
            "correlation-read",
            CancellationToken.None);
        await gateway.ExecuteAsync(
            "ansight_list_app_tools",
            new JsonObject { ["query"] = "map control", ["policy"] = "write" },
            "selected-session",
            "correlation-write",
            CancellationToken.None);

        Assert.Equal(2, dispatcher.Calls.Count);
        Assert.Equal("read", dispatcher.Calls[0].Arguments?["policy"]?.GetValue<string>());
        Assert.Equal("write", dispatcher.Calls[1].Arguments?["policy"]?.GetValue<string>());
    }

    [Fact]
    public async Task AppToolBridge_HidesAndRejectsInAppScreenshotTool()
    {
        var dispatcher = new RecordingOperationDispatcher();
        dispatcher.Responses["ansight_list_app_tools"] = RequestResult.ToolResult(
            new JsonObject
            {
                ["sessionId"] = "selected-session",
                ["appId"] = "com.example.app",
                ["catalog"] = new JsonObject
                {
                    ["tools"] = new JsonArray
                    {
                        AppTool(
                            "ui.get_screenshot",
                            "read",
                            executable: true)
                    }
                }
            },
            isError: false);
        var gateway = new ToolGateway(dispatcher);
        gateway.BeginRun("selected-session", SecretAccess.Empty);

        var discovery = await gateway.ExecuteAsync(
            "ansight_list_app_tools",
            new JsonObject { ["query"] = "screenshot" },
            "selected-session",
            "correlation-discovery",
            CancellationToken.None);
        var call = await gateway.ExecuteAsync(
            "ansight_call_app_tool",
            new JsonObject { ["toolId"] = "ui.get_screenshot" },
            "selected-session",
            "correlation-call",
            CancellationToken.None);

        Assert.DoesNotContain("ui.get_screenshot", discovery.Output, StringComparison.Ordinal);
        Assert.True(call.IsError);
        Assert.Single(dispatcher.Calls);
    }

    [Fact]
    public async Task AppToolBridge_PreservesPrerequisiteMetadataAndRemoteErrorMessage()
    {
        var queryTool = AppTool(
            "redpoint.mapbox.query_surface_contents",
            "read",
            executable: true);
        queryTool["prerequisiteToolIds"] = new JsonArray("redpoint.mapbox.list_surfaces");
        var dispatcher = new RecordingOperationDispatcher();
        dispatcher.Responses["ansight_list_app_tools"] = RequestResult.ToolResult(
            new JsonObject
            {
                ["sessionId"] = "selected-session",
                ["appId"] = "com.example.app",
                ["catalog"] = new JsonObject
                {
                    ["tools"] = new JsonArray
                    {
                        queryTool,
                        AppTool(
                            "redpoint.mapbox.list_surfaces",
                            "read",
                            executable: true)
                    }
                }
            },
            isError: false);
        dispatcher.Responses["ansight_call_app_tool"] = RequestResult.ToolResult(
            new JsonObject
            {
                ["sessionId"] = "selected-session",
                ["appId"] = "com.example.app",
                ["toolId"] = "redpoint.mapbox.query_surface_contents",
                ["responseType"] = "tool.error",
                ["payload"] = new JsonObject
                {
                    ["code"] = "redpoint_mapbox_surface_not_found",
                    ["message"] = "No live Mapbox surface matched the supplied selector.",
                    ["retryable"] = false
                }
            },
            isError: true);
        var gateway = new ToolGateway(dispatcher);
        gateway.BeginRun("selected-session", SecretAccess.Empty);

        var discovery = await gateway.ExecuteAsync(
            "ansight_list_app_tools",
            new JsonObject { ["query"] = "annotation manager" },
            "selected-session",
            "correlation-discovery",
            CancellationToken.None);
        var result = await gateway.ExecuteAsync(
            "ansight_call_app_tool",
            new JsonObject
            {
                ["toolId"] = "redpoint.mapbox.query_surface_contents",
                ["arguments"] = new JsonObject { ["surfaceId"] = "visual-tree-node-id" }
            },
            "selected-session",
            "correlation-call",
            CancellationToken.None);

        Assert.Contains("prerequisiteToolIds", discovery.Output, StringComparison.Ordinal);
        Assert.Contains("redpoint.mapbox.list_surfaces", discovery.Output, StringComparison.Ordinal);
        Assert.True(result.IsError);
        Assert.Equal("No live Mapbox surface matched the supplied selector.", result.Message);
        Assert.Contains("redpoint_mapbox_surface_not_found", result.Output, StringComparison.Ordinal);
    }

    private static JsonObject ToolDefinition(
        string name,
        string description,
        params string[] propertyNames)
    {
        var properties = new JsonObject();
        foreach (var propertyName in propertyNames)
        {
            properties[propertyName] = new JsonObject { ["type"] = "string" };
        }

        return new JsonObject
        {
            ["name"] = name,
            ["description"] = description,
            ["inputSchema"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = properties,
                ["additionalProperties"] = false
            }
        };
    }

    private static JsonObject AppTool(
        string toolId,
        string policy,
        bool executable)
        => new()
        {
            ["id"] = toolId,
            ["name"] = toolId,
            ["description"] = $"Description for {toolId}.",
            ["category"] = "example",
            ["policy"] = policy,
            ["executable"] = executable,
            ["argumentsSchema"] = new JsonObject
            {
                ["type"] = "object",
                ["additionalProperties"] = false
            }
        };

    private static SessionCapabilities BuildCapabilities(
        string platform,
        params string[] toolIds)
        => SessionCapabilities.FromPublishedTools(
            platform,
            new JsonObject
            {
                ["tools"] = new JsonArray(
                    toolIds.Select(toolId => (JsonNode?)AppTool(toolId, "read", executable: true)).ToArray())
            });

    private static string BuildContextSnapshot(SessionCapabilities capabilities)
    {
        var dispatcher = new RecordingOperationDispatcher(
            ToolDefinition(
                "ansight_get_live_visual_tree",
                "Get live tree",
                "sessionId",
                "appId",
                "toolId",
                "arguments",
                "root",
                "includeBounds",
                "includeProperties",
                "includeBindableProperties",
                "includeBindingContexts",
                "includeInactivePages",
                "includeComputedStyles",
                "includeProps",
                "includeState",
                "maxDepth",
                "maxNodes"),
            ToolDefinition(
                "ansight_get_live_navigation_structure",
                "Get navigation structure",
                "sessionId",
                "appId",
                "framework"));
        var gateway = new ToolGateway(dispatcher);
        var tools = gateway.BuildOpenAiToolDefinitions(
            capabilities: capabilities,
            appGraphEnabled: false);
        var visualTree = Assert.Single(tools.OfType<JsonObject>(), tool =>
            tool["name"]?.GetValue<string>() == "ansight_get_live_visual_tree");
        var navigation = tools.OfType<JsonObject>().SingleOrDefault(tool =>
            tool["name"]?.GetValue<string>() == "ansight_get_live_navigation_structure");
        var prompt = SimulatorAgentService.BuildAgentInstructions(
            hasUsableAppGraphRoute: false,
            isAppGraphExploration: false,
            capabilities);
        var completeContext = prompt + "\n" + tools.ToJsonString();
        var knownTechnologies = new[] { ".NET MAUI", "React Native", "Flutter", "DOM" };
        var contextTechnologies = knownTechnologies
            .Where(technology => completeContext.Contains(technology, StringComparison.Ordinal))
            .ToArray();

        return string.Join(
            "\n",
            $"profile={capabilities.ProfileDescription}",
            $"visual.providers={string.Join(',', ReadEnum(visualTree, "toolId"))}",
            $"visual.parameters={string.Join(',', ReadParameterNames(visualTree))}",
            $"navigation.frameworks={string.Join(',', navigation is null ? [] : ReadEnum(navigation, "framework"))}",
            $"navigation.parameters={string.Join(',', navigation is null ? [] : ReadParameterNames(navigation))}",
            $"context.technologies={string.Join(',', contextTechnologies)}",
            $"context.appGraph={completeContext.Contains("App Graph", StringComparison.Ordinal)}");
    }

    private static IReadOnlyList<string> ReadEnum(JsonObject tool, string propertyName)
        => tool["parameters"]?["properties"]?[propertyName]?["enum"] is JsonArray values
            ? values.GetValues<string>().ToArray()
            : [];

    private static IReadOnlyList<string> ReadParameterNames(JsonObject tool)
        => tool["parameters"]?["properties"] is JsonObject properties
            ? properties.Select(static property => property.Key)
                .Order(StringComparer.Ordinal)
                .ToArray()
            : [];

    private static AppSessionSnapshot CreateSessionSnapshot(
        string sessionId,
        string appId,
        DateTimeOffset createdUtc,
        IReadOnlyList<SessionVisualTreeSnapshot>? visualTreeSnapshots = null,
        string captureSource = WorkspaceExecutionModes.Sdk)
        => new()
        {
            SessionId = sessionId,
            AppId = appId,
            ClientName = "Example App",
            RemoteAddress = "127.0.0.1",
            CreatedUtc = createdUtc,
            ConfigId = null,
            Status = "WebSocket Open",
            LastUpdatedUtc = createdUtc,
            IsHistorical = false,
            CaptureSource = captureSource,
            VisualTreeSnapshots = visualTreeSnapshots ?? [],
            MetricChannels = [],
            Metrics = []
        };

    private sealed record RecordedOperationCall(
        string ToolName,
        JsonObject? Arguments,
        string? CorrelationId);

    private sealed class RecordingOperationDispatcher : IOperationDispatcher
    {
        public IAppInteractionContext CreateAppInteractionContext(string sessionId, string? repositoryRootPath = null)
            => throw new NotSupportedException();

        private readonly JsonArray tools;

        public RecordingOperationDispatcher(params JsonObject[] tools)
        {
            this.tools = new JsonArray(tools.Select(static tool => (JsonNode?)tool).ToArray());
        }

        public string? ActiveSimulatorAgentSessionId { get; private set; }

        public string? LastToolName { get; private set; }

        public JsonObject? LastArguments { get; private set; }

        public Dictionary<string, RequestResult> Responses { get; } = new(StringComparer.Ordinal);

        public Func<string, JsonObject?, RequestResult>? ResponseFactory { get; set; }

        public List<RecordedOperationCall> Calls { get; } = [];

        public List<AppSessionSnapshot> SessionSnapshots { get; } = [];

        public HashSet<string> ConnectedSessionIds { get; } = new(StringComparer.Ordinal);

        public JsonObject? AppToolCatalog { get; set; }

        public JsonObject BuildToolsListResult() => new() { ["tools"] = tools.DeepClone() };

        public IReadOnlyList<AppSessionSnapshot> GetSessionSummaries() => SessionSnapshots;

        public bool TryGetSessionSnapshot(string sessionId, out AppSessionSnapshot? snapshot)
        {
            snapshot = SessionSnapshots.FirstOrDefault(candidate => string.Equals(
                candidate.SessionId,
                sessionId,
                StringComparison.Ordinal));
            return snapshot is not null;
        }

        public bool IsSessionConnected(string sessionId) => ConnectedSessionIds.Contains(sessionId);

        public Task<JsonObject?> GetSessionAppToolCatalogAsync(
            string sessionId,
            CancellationToken cancellationToken)
            => Task.FromResult(AppToolCatalog?.DeepClone().AsObject());

        public Task<RequestResult> CallToolAsync(
            string toolName,
            JsonObject? arguments,
            string? correlationId = null,
            OperationExecutionContext? context = null)
        {
            LastOperationContext = context;
            LastToolName = toolName;
            LastArguments = arguments?.DeepClone().AsObject();
            Calls.Add(new RecordedOperationCall(
                toolName,
                arguments?.DeepClone().AsObject(),
                correlationId));
            return Task.FromResult(ResponseFactory?.Invoke(toolName, arguments)
                ?? (Responses.TryGetValue(toolName, out var response)
                ? response
                : RequestResult.ToolResult(
                    new JsonObject { ["message"] = "ok" },
                    isError: false)));
        }

        public OperationExecutionContext? LastOperationContext { get; private set; }

        public IDisposable BeginSimulatorAgentRun(string sessionId, string? targetDeviceIdentifier = null)
        {
            ActiveSimulatorAgentSessionId = sessionId;
            return new CallbackDisposable(() => ActiveSimulatorAgentSessionId = null);
        }

        public void ConfigureAudioInjection(Ansight.Host.Audio.AudioInjectionEngine engine) { }

        public void ConfigureUiInputDriver(IUiInputDriver? driver)
        {
        }

        public void ConfigureUiAccessibilityDriver(IUiAccessibilityDriver? driver)
        {
        }

        public void ConfigureDeviceLocationPlayback(DeviceLocationPlaybackService playback, IDeviceService devices)
        {
        }

        public void ConfigureDeviceLocationDriver(IDeviceLocationDriver? driver)
        {
        }

        public void ConfigureDeviceLifecycleDriver(IDeviceLifecycleDriver? driver)
        {
        }

        public void ConfigureRepositoryTaskRuntime(string executablePath)
        {
        }

        public RepositoryTaskCatalog InspectRepositoryTasks(string repositoryRootPath, string appId)
            => throw new NotSupportedException();

        public Task<RepositoryTaskRunResult> RunRepositoryTaskAsync(
            string repositoryRootPath,
            string appId,
            string sessionId,
            string taskId,
            JsonObject? suppliedInput,
            CancellationToken cancellationToken,
            IReadOnlyDictionary<string, string>? secretValues = null)
            => throw new NotSupportedException();

        public void Dispose()
        {
        }

        private sealed class CallbackDisposable(Action callback) : IDisposable
        {
            private Action? callback = callback;

            public void Dispose()
            {
                Interlocked.Exchange(ref callback, null)?.Invoke();
            }
        }
    }
}
