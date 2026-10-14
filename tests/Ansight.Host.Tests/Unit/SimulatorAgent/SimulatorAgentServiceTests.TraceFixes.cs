using System.Text.Json.Nodes;

namespace Ansight.Host.Tests.Unit.SimulatorAgent;

public sealed partial class SimulatorAgentServiceTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RunAsync_RelatedTaskIsConsideredButOtherUnusedTasksStillRequireAssessment(bool includeOther)
    {
        var gateway = new FakeToolGateway { RepositoryTaskShortcuts = [
            CreateRepositoryTaskShortcut("ansight_task_map", "map", "Map"),
            CreateRepositoryTaskShortcut("ansight_task_auth", "auth", "Auth")] };
        AddToolLoadingDefinitions(gateway, "ansight_find_ui");
        var declaration = new JsonObject
        {
            ["uncoveredStep"] = "Select Map.", ["reason"] = "starting-state-not-satisfied",
            ["relatedTaskId"] = "map", ["evidence"] = "The initial observation has For you selected.",
            ["consideredTaskIds"] = includeOther ? new JsonArray("auth") : new JsonArray(),
            ["taskDecisions"] = new JsonArray()
        };
        var session = new FakeOpenAiSession([
            CreateFunctionTurn("declare", "ansight_declare_uncovered_step", declaration),
            CreateFunctionTurn("find", "ansight_find_ui", new JsonObject { ["text"] = "Map" }),
            CompleteWebSocketTurn()]);
        using var service = CreateToolLoadingService(session, gateway);
        var result = await service.RunAsync(ToolLoadingRequest("Select Map.") with { CaptureTrace = true });
        var assessed = Assert.Single(result.Audit.ToolCalls, c => c.CallId == "declare");
        Assert.Equal(!includeOther, assessed.IsError);
        Assert.Equal(includeOther ? 1 : 0, gateway.Calls.Count);
        if (!includeOther) Assert.Contains("Missing task IDs: auth", assessed.Message);
        var loader = session.Requests[0].Tools.OfType<JsonObject>()
            .Single(t => t["name"]?.GetValue<string>() == AgentToolCatalog.LoadToolName);
        Assert.DoesNotContain("manual-ui", loader["parameters"]!["properties"]!["bundle"]!["enum"]!
            .AsArray().Select(v => v!.GetValue<string>()));
    }

    [Theory]
    [InlineData("scope-mismatch")]
    [InlineData("starting-state-not-satisfied")]
    public async Task RunAsync_UnknownRelatedTaskStillFailsBeforeManualToolsAreLoaded(string reason)
    {
        var gateway = new FakeToolGateway { RepositoryTaskShortcuts = [CreateRepositoryTaskShortcut("ansight_task_map", "map", "Map")] };
        AddToolLoadingDefinitions(gateway, "ansight_tap_ui");
        var session = new FakeOpenAiSession([CreateFunctionTurn("declare", "ansight_declare_uncovered_step", new JsonObject
        {
            ["uncoveredStep"] = "Select Map.", ["reason"] = reason, ["relatedTaskId"] = "unknown",
            ["evidence"] = "For you is selected.", ["consideredTaskIds"] = new JsonArray("map")
        }), CompleteWebSocketTurn()]);
        using var service = CreateToolLoadingService(session, gateway);
        var result = await service.RunAsync(ToolLoadingRequest("Select Map.") with { CaptureTrace = true });
        Assert.True(Assert.Single(result.Audit.ToolCalls, c => c.CallId == "declare").IsError);
        Assert.Empty(gateway.Calls);
        Assert.DoesNotContain(AdditionalToolItems(session.Requests[1].IncrementalInput!),
            item => ToolNames(item["tools"]!.AsArray()).Contains("ansight_tap_ui"));
    }

    [Fact]
    public void UiBatchSchema_PreviousTargetOffersOnlyActionOptions()
    {
        var definition = AgentToolBatch.BuildUiDefinition(new JsonArray(
            new JsonObject { ["name"] = "ansight_find_ui" },
            new JsonObject { ["name"] = "ansight_tap_ui" },
            new JsonObject { ["name"] = "ansight_type_text", ["parameters"] = new JsonObject
            { ["properties"] = new JsonObject { ["value"] = new JsonObject { ["type"] = "string" },
                ["index"] = new JsonObject { ["type"] = "integer" } } } }));
        var variants = definition!["parameters"]!["properties"]!["steps"]!["items"]!["anyOf"]!.AsArray();
        Assert.Equal(3, variants.Count);
        foreach (var variant in variants.Skip(1))
        {
            var arguments = variant!["properties"]!["arguments"]!;
            Assert.False(arguments["additionalProperties"]!.GetValue<bool>());
            Assert.DoesNotContain("index", arguments["properties"]!.AsObject().Select(p => p.Key));
            Assert.True(variant["properties"]!["usePreviousTarget"]!["enum"]![0]!.GetValue<bool>());
        }
        Assert.Contains("value", variants[2]!["properties"]!["arguments"]!["required"]!.AsArray().Select(v => v!.GetValue<string>()));
    }
}
