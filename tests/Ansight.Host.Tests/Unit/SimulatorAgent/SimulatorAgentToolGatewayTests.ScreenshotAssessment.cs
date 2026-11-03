using System.Text.Json.Nodes;

namespace Ansight.Host.Tests.Unit.SimulatorAgent;

public sealed partial class SimulatorAgentToolGatewayTests
{
    [Fact]
    public async Task ScreenshotAssessmentUsesSelectedSessionAndRetainedFrameReferences()
    {
        var tool = ToolDefinition("ansight_evaluate_screenshot", "Assess pixels",
            "sessionId", "appId", "teamId", "image", "criteria", "reference");
        tool["inputSchema"]!["properties"]!["reference"] = new JsonObject
        {
            ["type"] = new JsonArray("object", "null"),
            ["properties"] = new JsonObject
            {
                ["frameId"] = new JsonObject { ["type"] = "string" },
                ["workspacePath"] = new JsonObject { ["type"] = "string" }
            }
        };
        var dispatcher = new RecordingOperationDispatcher(tool);
        var gateway = new ToolGateway(dispatcher);
        var definition = Assert.Single(gateway.BuildOpenAiToolDefinitions().OfType<JsonObject>());
        var properties = definition["parameters"]!["properties"]!.AsObject();
        Assert.DoesNotContain("sessionId", properties);
        Assert.DoesNotContain("appId", properties);
        Assert.DoesNotContain("teamId", properties);
        Assert.DoesNotContain("workspacePath", properties["reference"]!["properties"]!.AsObject());

        var arguments = new JsonObject
        {
            ["sessionId"] = "other-session",
            ["appId"] = "other-app",
            ["teamId"] = "other-team",
            ["image"] = new JsonObject { ["frameId"] = "after" },
            ["reference"] = new JsonObject { ["frameId"] = "before" },
            ["criteria"] = new JsonArray(new JsonObject
            {
                ["id"] = "closed", ["description"] = "The editor is closed."
            })
        };
        var result = await gateway.ExecuteAsync("ansight_evaluate_screenshot", arguments,
            "selected-session", "assess", CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal("selected-session", dispatcher.LastArguments!["sessionId"]!.GetValue<string>());
        Assert.False(dispatcher.LastArguments.ContainsKey("appId"));
        Assert.False(dispatcher.LastArguments.ContainsKey("teamId"));
        Assert.True(JsonNode.DeepEquals(arguments["image"], dispatcher.LastArguments["image"]));
        Assert.True(JsonNode.DeepEquals(arguments["criteria"], dispatcher.LastArguments["criteria"]));
        Assert.True(JsonNode.DeepEquals(arguments["reference"], dispatcher.LastArguments["reference"]));

        arguments["reference"] = new JsonObject { ["workspacePath"] = "references/other.png" };
        var rejected = await gateway.ExecuteAsync("ansight_evaluate_screenshot", arguments,
            "selected-session", "reject", CancellationToken.None);
        Assert.True(rejected.IsError);
        Assert.Single(dispatcher.Calls);

        var catalog = new AgentToolCatalog(new JsonArray(definition.DeepClone()));
        Assert.True(catalog.IsDeferred("ansight_evaluate_screenshot"));
        var loaded = catalog.Load(new JsonObject { ["bundle"] = "evidence" });
        Assert.False(loaded.Result.IsError);
        Assert.False(catalog.IsDeferred("ansight_evaluate_screenshot"));
    }
}
