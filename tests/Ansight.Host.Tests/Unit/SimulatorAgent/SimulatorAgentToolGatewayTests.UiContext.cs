using System.Text.Json.Nodes;
using Ansight.Host.Runtime.Operations;

namespace Ansight.Host.Tests.Unit.SimulatorAgent;

public sealed partial class SimulatorAgentToolGatewayTests
{
    [Theory]
    [InlineData("ansight_find_ui", "matches")]
    [InlineData("ansight_wait_for_ui", "matches")]
    [InlineData("ansight_assert_ui", "selected")]
    [InlineData("ansight_tap_ui", "target")]
    public async Task UiResults_ShareProjectionWhileRetainingOriginalEvidence(string toolName, string nodeField)
    {
        const string runtimeId = "f1681cbb-d68d-4b99-9519-27ae54cbe7a2";
        var node = new JsonObject
        {
            ["id"] = runtimeId,
            ["automationId"] = "ready-control",
            ["role"] = "button",
            ["bounds"] = new JsonObject { ["x"] = 10, ["y"] = 20, ["width"] = 30, ["height"] = 40 },
            ["visual"] = new JsonObject { ["debug"] = new string('x', 2_000) },
            ["ancestorPath"] = new JsonArray(new JsonObject { ["id"] = "page", ["automationId"] = "ReadyPage" }),
            ["tapHint"] = new JsonObject { ["selector"] = new JsonObject { ["nodeId"] = runtimeId } }
        };
        var payload = new JsonObject
        {
            [nodeField] = nodeField == "matches" ? new JsonArray(node) : node,
            ["totalMatches"] = 1,
            ["satisfied"] = true,
            ["passed"] = false,
            ["performed"] = false,
            ["failures"] = new JsonArray("Expected enabled control."),
            ["evidence"] = new JsonObject { ["before"] = new JsonObject { ["treeHash"] = "before-hash" } }
        };
        var original = payload.ToJsonString();
        var dispatcher = new RecordingOperationDispatcher();
        dispatcher.Responses[toolName] = RequestResult.ToolResult(payload, isError: true);
        var gateway = new ToolGateway(dispatcher);
        gateway.BeginRun("session", SecretAccess.Empty);
        try
        {
            var response = await gateway.ExecuteAsync(toolName, new JsonObject(), "session", "project", default);
            var model = JsonNode.Parse(response.ModelOutput!)!["result"]!.AsObject();
            var projectedNode = nodeField == "matches" ? model[nodeField]![0]! : model[nodeField]!;
            Assert.True(response.IsError);
            Assert.False(model["performed"]!.GetValue<bool>());
            Assert.False(model["passed"]!.GetValue<bool>());
            Assert.True(model["satisfied"]!.GetValue<bool>());
            Assert.Equal(1, model["totalMatches"]!.GetValue<int>());
            Assert.Equal("Expected enabled control.", model["failures"]![0]!.GetValue<string>());
            Assert.Equal("before-hash", model["evidence"]!["before"]!["treeHash"]!.GetValue<string>());
            Assert.Null(projectedNode["visual"]);
            Assert.Null(projectedNode["ancestorPath"]);
            Assert.Equal("page", projectedNode["parentId"]!.GetValue<string>());
            Assert.Equal("[10,20,30,40]", projectedNode["bounds"]!.ToJsonString());
            Assert.Equal(projectedNode["id"]!.GetValue<string>(), projectedNode["tapHint"]!["selector"]!["nodeId"]!.GetValue<string>());
            Assert.DoesNotContain(runtimeId, response.ModelOutput!);
            Assert.Contains(runtimeId, response.Output);
            Assert.Contains("ancestorPath", response.Output);
            Assert.Contains("debug", response.Output);
            Assert.Equal(original, payload.ToJsonString());
        }
        finally { gateway.EndRun(); }
    }

    [Fact]
    public async Task UiAliases_ResolveBeforeDispatchAndRejectExpiredAliasesWithoutDispatch()
    {
        const string runtimeId = "f1681cbb-d68d-4b99-9519-27ae54cbe7a2";
        var dispatcher = new RecordingOperationDispatcher();
        dispatcher.Responses["ansight_find_ui"] = RequestResult.ToolResult(new JsonObject
        {
            ["matches"] = new JsonArray(new JsonObject
            {
                ["id"] = runtimeId, ["role"] = "button", ["supportedActions"] = new JsonArray("tap")
            })
        }, isError: false);
        var gateway = new ToolGateway(dispatcher);
        gateway.BeginRun("session", SecretAccess.Empty);
        try
        {
            var observation = await gateway.ExecuteAsync("ansight_find_ui", new JsonObject(), "session", "find", default);
            var alias = JsonNode.Parse(observation.ModelOutput!)!["result"]!["matches"]![0]!["id"]!.GetValue<string>();
            Assert.Contains(runtimeId, observation.Output);
            Assert.DoesNotContain(runtimeId, observation.ModelOutput!);
            var arguments = new JsonObject { ["nodeId"] = alias };
            var action = await gateway.ExecuteAsync("ansight_tap_ui", arguments, "session", "tap", default);
            Assert.False(action.IsError);
            Assert.Equal(runtimeId, dispatcher.LastArguments!["nodeId"]!.GetValue<string>());
            Assert.Equal(alias, arguments["nodeId"]!.GetValue<string>());
            var count = dispatcher.Calls.Count;
            var stale = await gateway.ExecuteAsync("ansight_tap_ui", arguments, "session", "stale", default);
            Assert.True(stale.IsError);
            Assert.Contains("expired", stale.Message);
            Assert.Equal(count, dispatcher.Calls.Count);
        }
        finally { gateway.EndRun(); }
    }

    [Fact]
    public async Task InitialCapture_UsesCurrentPageAndAppliesNormalPrivacyLimits()
    {
        var dispatcher = new RecordingOperationDispatcher();
        var gateway = new ToolGateway(dispatcher);
        await gateway.CaptureInitialObservationAsync("session",
            new SessionCapabilities("ios", ["maui.get_visual_tree"], []), "initial", default);
        Assert.Equal("ansight_get_live_visual_tree", dispatcher.LastToolName);
        Assert.Equal("currentPage", dispatcher.LastArguments!["root"]!.GetValue<string>());
        Assert.Equal("maui.get_visual_tree", dispatcher.LastArguments["toolId"]!.GetValue<string>());
        Assert.False(dispatcher.LastArguments["includeBindingContexts"]!.GetValue<bool>());
        Assert.False(dispatcher.LastArguments["includeInactivePages"]!.GetValue<bool>());
    }
}
