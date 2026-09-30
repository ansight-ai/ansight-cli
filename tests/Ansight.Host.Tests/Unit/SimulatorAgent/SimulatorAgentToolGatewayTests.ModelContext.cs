using System.Text.Json.Nodes;
using Ansight.Host.Runtime.Operations;

namespace Ansight.Host.Tests.Unit.SimulatorAgent;

public sealed partial class SimulatorAgentToolGatewayTests
{
    [Theory]
    [InlineData("ansight_task_open_account_0123456789ab", "open-account")]
    [InlineData("open-account", "open-account")]
    [InlineData("ansight_task_unknown_0123456789ab", "ansight_task_unknown_0123456789ab")]
    public void CanonicalTaskCall_NormalizesOnlyKnownShortcutIdsAndPreservesArguments(string suppliedTaskId, string expectedTaskId)
    {
        var gateway = new ToolGateway(new RecordingOperationDispatcher());
        gateway.BeginRun("session", SecretAccess.Empty);
        try
        {
            gateway.BuildOpenAiToolDefinitions([new RepositoryTaskShortcut(
                "ansight_task_open_account_0123456789ab", "open-account", "Open account", "Open the account page.", null,
                new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() }, 90, 1)]);
            var arguments = new JsonObject
            {
                ["taskId"] = suppliedTaskId,
                ["input"] = new JsonObject { ["account"] = "selected", ["options"] = new JsonObject { ["verify"] = true } },
                ["extra"] = "retained"
            };
            var original = arguments.ToJsonString();
            var call = new OpenAiFunctionCall("call-id", "ansight_run_task", arguments);

            var normalized = gateway.NormalizeFunctionCall(call);

            Assert.Equal("call-id", normalized.CallId);
            Assert.Equal("ansight_run_task", normalized.Name);
            Assert.Equal(expectedTaskId, normalized.Arguments["taskId"]!.GetValue<string>());
            Assert.Equal(arguments["input"]!.ToJsonString(), normalized.Arguments["input"]!.ToJsonString());
            Assert.Equal("retained", normalized.Arguments["extra"]!.GetValue<string>());
            Assert.Equal(original, arguments.ToJsonString());
        }
        finally
        {
            gateway.EndRun();
        }
    }

    [Fact]
    public void ModelSchemas_RemoveOverriddenOptionsAndAliasesWithoutChangingPublicTools()
    {
        var swipe = ToolDefinition("ansight_swipe_ui", "Swipe", "sessionId", "includeScreenshot", "direction", "distance", "orientation", "length", "ancestorAutomationId", "startNormalizedX");
        swipe["inputSchema"]!["required"] = new JsonArray("sessionId", "direction", "orientation");
        swipe["inputSchema"]!["properties"]!["orientation"]!["additionalProperties"] = false;
        var find = ToolDefinition("ansight_find_ui", "Find", "exact", "index", "matchMode", "visible", "role");
        var tree = ToolDefinition("ansight_get_live_visual_tree", "Tree", "includeProperties", "includeBindableProperties", "includeBindingContexts", "includeInactivePages", "includeBounds");
        var module = ToolDefinition("ansight_describe_module", "Describe", "includeDefinitions", "moduleId");
        var appTools = ToolDefinition("ansight_list_app_tools", "Search", "executableOnly", "query");
        var dispatcher = new RecordingOperationDispatcher(swipe, find, tree, module, appTools);
        var original = dispatcher.BuildToolsListResult().ToJsonString();
        var gateway = new ToolGateway(dispatcher);

        var tools = gateway.BuildOpenAiToolDefinitions().OfType<JsonObject>()
            .ToDictionary(tool => tool["name"]!.GetValue<string>());

        var swipeSchema = tools["ansight_swipe_ui"]["parameters"]!;
        var swipeProperties = swipeSchema["properties"]!.AsObject();
        Assert.Equal(["orientation"], swipeSchema["required"]!.AsArray().GetValues<string>());
        Assert.DoesNotContain("sessionId", swipeProperties);
        Assert.DoesNotContain("includeScreenshot", swipeProperties);
        Assert.DoesNotContain("direction", swipeProperties);
        Assert.DoesNotContain("distance", swipeProperties);
        Assert.Contains("ancestorAutomationId", swipeProperties);
        Assert.Contains("startNormalizedX", swipeProperties);
        Assert.Null(swipeProperties["orientation"]!["additionalProperties"]);
        Assert.False(swipeSchema["additionalProperties"]!.GetValue<bool>());
        var findProperties = tools["ansight_find_ui"]["parameters"]!["properties"]!.AsObject();
        Assert.DoesNotContain("exact", findProperties);
        Assert.DoesNotContain("index", findProperties);
        Assert.Contains("matchMode", findProperties);
        Assert.Contains("does not imply on-screen", findProperties["visible"]!["description"]!.GetValue<string>());
        Assert.Equal(["includeBounds"], tools["ansight_get_live_visual_tree"]["parameters"]!["properties"]!.AsObject().Select(property => property.Key));
        Assert.Null(tools["ansight_describe_module"]["parameters"]!["properties"]!["includeDefinitions"]);
        Assert.Null(tools["ansight_list_app_tools"]["parameters"]!["properties"]!["executableOnly"]);
        Assert.Equal(original, dispatcher.BuildToolsListResult().ToJsonString());
    }

    [Fact]
    public void ModelSchemas_PreserveNullableObjectsAndTaskPropertyNames()
    {
        var task = new RepositoryTaskShortcut(
            "ansight_task_sample", "sample", "Sample", "Sample task", null,
            JsonNode.Parse("""
                {"type":"object","additionalProperties":false,"properties":{
                  "type":{"type":"string","additionalProperties":false},
                  "additionalProperties":{"type":"string","additionalProperties":false},
                  "settings":{"type":["object","null"],"additionalProperties":true},
                  "values":{"type":"array","additionalProperties":false,"items":{"type":["string","null"],"additionalProperties":false}}
                }}
                """)!.AsObject(), 90, 1);
        var original = task.InputSchema.ToJsonString();
        var gateway = new ToolGateway(new RecordingOperationDispatcher());

        var definition = Assert.Single(gateway.BuildOpenAiToolDefinitions([task]).OfType<JsonObject>());
        var properties = definition["parameters"]!["properties"]!.AsObject();

        Assert.Contains("additionalProperties", properties);
        Assert.Null(properties["type"]!["additionalProperties"]);
        Assert.True(properties["settings"]!["additionalProperties"]!.GetValue<bool>());
        Assert.Null(properties["values"]!["additionalProperties"]);
        Assert.Null(properties["values"]!["items"]!["additionalProperties"]);
        Assert.Equal(original, task.InputSchema.ToJsonString());
    }

    [Fact]
    public async Task RepositoryShortcutNames_AreStableAcrossRanksAndDistinctAfterNormalizationOrTruncation()
    {
        var taskIds = new[] { "open-weather", "open_weather", "open-weather-" + new string('a', 80), "open-weather-" + new string('a', 79) + "b" };
        var reversed = false;
        var dispatcher = new RecordingOperationDispatcher();
        dispatcher.ResponseFactory = (_, _) => RequestResult.ToolResult(new JsonObject
        {
            ["tasks"] = new JsonArray((reversed ? taskIds.Reverse() : taskIds)
                .Select((taskId, index) => (JsonNode?)new JsonObject
                {
                    ["taskId"] = taskId,
                    ["title"] = "Open weather",
                    ["description"] = "Open weather for the current area.",
                    ["inputSchema"] = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject(), ["additionalProperties"] = false },
                    ["match"] = new JsonObject { ["score"] = 90.0 - index, ["coverage"] = 1.0 }
                }).ToArray())
        }, isError: false);
        var gateway = new ToolGateway(dispatcher);

        var first = await gateway.GetRepositoryTaskShortcutsAsync("session", "Open weather", CancellationToken.None);
        reversed = true;
        var second = await gateway.GetRepositoryTaskShortcutsAsync("session", "Open weather", CancellationToken.None);

        Assert.Equal(taskIds.Length, first.Count);
        Assert.Equal(taskIds.Length, first.Select(task => task.ToolName).Distinct().Count());
        foreach (var task in first)
        {
            Assert.InRange(task.ToolName.Length, 1, 64);
            Assert.Matches("^[a-z0-9_]+$", task.ToolName);
            Assert.Equal(task.ToolName, Assert.Single(second, candidate => candidate.TaskId == task.TaskId).ToolName);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RepositoryResults_ModelRetainsOutcomesAndFailureEvidenceWhileAuditKeepsFullHistory(bool failed)
    {
        var calls = new JsonArray(Enumerable.Range(1, 100).Select(index => (JsonNode?)new JsonObject
        {
            ["sequence"] = index,
            ["toolName"] = "ansight_wait_for_ui",
            ["durationMilliseconds"] = 123,
            ["isError"] = failed && index == 100,
            ["message"] = index == 100 && failed ? "Destination did not appear." : new string('x', 150)
        }).ToArray());
        var content = new JsonObject
        {
            ["taskId"] = "example",
            ["status"] = failed ? "Failed" : "Passed",
            ["runId"] = "audit-id",
            ["message"] = failed ? "Destination assertion failed." : "Destination verified.",
            ["output"] = new JsonObject { ["destination"] = "Details", ["ready"] = !failed },
            ["assertions"] = new JsonArray(new JsonObject { ["assertionId"] = "destination-ready", ["passed"] = !failed, ["message"] = "Expected Details ready state." }),
            ["toolCalls"] = calls
        };
        if (failed)
        {
            content["standardError"] = "AssertionError: destination-ready";
        }
        var dispatcher = new RecordingOperationDispatcher();
        dispatcher.Responses["ansight_run_task"] = RequestResult.ToolResult(content, isError: failed);
        var gateway = new ToolGateway(dispatcher);

        var result = await gateway.ExecuteAsync("ansight_run_task", new JsonObject { ["taskId"] = "example" }, "session", "correlation", CancellationToken.None);

        Assert.Equal(failed, result.IsError);
        var audit = JsonNode.Parse(result.Output)!;
        Assert.Equal(100, audit["result"]!["toolCalls"]!.AsArray().Count);
        Assert.Null(audit["truncated"]);
        var model = JsonNode.Parse(Assert.IsType<string>(result.ModelOutput))!;
        Assert.Equal("example", model["result"]!["taskId"]!.GetValue<string>());
        Assert.Equal(failed ? "Failed" : "Passed", model["result"]!["status"]!.GetValue<string>());
        Assert.Equal("Details", model["result"]!["output"]!["destination"]!.GetValue<string>());
        Assert.Equal(!failed, model["result"]!["assertions"]![0]!["passed"]!.GetValue<bool>());
        Assert.Null(model["result"]!["toolCalls"]);
        Assert.Null(model["result"]!["runId"]);
        Assert.True(result.ModelOutput!.Length < result.Output.Length / 10);
        if (failed)
        {
            var failure = Assert.Single(model["result"]!["failureContext"]!.AsArray());
            Assert.Equal("Destination did not appear.", failure!["message"]!.GetValue<string>());
            Assert.Equal("AssertionError: destination-ready", model["result"]!["standardError"]!.GetValue<string>());
        }
    }
}
