using System.Text.Json.Nodes;

namespace Ansight.Host.Tests.Unit.SimulatorAgent;

public sealed class SimulatorAgentUiContextTests
{
    [Fact]
    public void QueryAncestors_AreSharedByIdentityAndTapHintsRemainUsable()
    {
        var original = JsonNode.Parse("""
            {"matches":[
              {"id":"a","text":"A","ancestorPath":[{"id":"page","automationId":"Page"},{"id":"group","automationId":"Group"}],"tapHint":{"tool":"ansight_tap_ui","selector":{"automationId":"Group"}}},
              {"id":"b","text":"B","ancestorPath":[{"id":"page","automationId":"Page"},{"id":"group","automationId":"Group"}],"tapHint":{"tool":"ansight_tap_ui","selector":{"automationId":"Group"}}}
            ]}
            """)!.AsObject();
        var before = original.ToJsonString();
        var model = UiResultProjection.Project(original, UiProjectionOptions.Model);
        Assert.DoesNotContain("ancestorPath", model.ToJsonString());
        Assert.Equal(2, model["ancestors"]!.AsArray().Count);
        Assert.All(model["matches"]!.AsArray().OfType<JsonObject>(), match =>
        {
            Assert.Equal("group", match["parentId"]!.GetValue<string>());
            Assert.Equal("Group", match["tapHint"]!["selector"]!["automationId"]!.GetValue<string>());
        });
        Assert.Equal(before, original.ToJsonString());
    }

    [Fact]
    public void Project_PreservesAutomationIdsAndCoordinatesAndResolvesDuplicateControls()
    {
        const string first = "a6512056-b9c7-4be0-b869-ec08577f4da6";
        const string second = "36bbc35e-5a39-4e4e-b231-ef74b5befb4f";
        var original = JsonNode.Parse($$$$"""
            {"matches":[
              {"id":"{{{{first}}}}","automationId":"duplicate-toggle","bounds":{"x":8,"y":48.5,"width":48,"height":48},"tapHint":{"selector":{"nodeId":"{{{{first}}}}"}}},
              {"id":"{{{{second}}}}","automationId":"duplicate-toggle"}
            ]}
            """)!.AsObject();
        var before = original.ToJsonString();
        var normalized = UiResultProjection.Project(original, UiProjectionOptions.Model);
        var normalizedBefore = normalized.ToJsonString();
        var context = new ModelUiContext();
        var model = context.Project(normalized);
        Assert.Equal(before, original.ToJsonString());
        Assert.Equal(normalizedBefore, normalized.ToJsonString());
        Assert.Equal(first, normalized["matches"]![0]!["id"]!.GetValue<string>());
        Assert.Equal("duplicate-toggle", model["matches"]![0]!["automationId"]!.GetValue<string>());
        Assert.Equal("[8,48.5,48,48]", model["matches"]![0]!["bounds"]!.ToJsonString());
        Assert.DoesNotContain(first, model.ToJsonString());
        var firstAlias = model["matches"]![0]!["id"]!.GetValue<string>();
        var secondAlias = model["matches"]![1]!["id"]!.GetValue<string>();
        Assert.NotEqual(firstAlias, secondAlias);
        Assert.Equal(firstAlias, model["matches"]![0]!["tapHint"]!["selector"]!["nodeId"]!.GetValue<string>());
        var arguments = new JsonObject { ["steps"] = new JsonArray(
            new JsonObject { ["nodeId"] = firstAlias }, new JsonObject { ["nodeId"] = secondAlias }) };
        Assert.True(context.TryResolveSelectors(arguments, out _));
        Assert.Equal(first, arguments["steps"]![0]!["nodeId"]!.GetValue<string>());
        Assert.Equal(second, arguments["steps"]![1]!["nodeId"]!.GetValue<string>());
        context.Invalidate();
        var fresh = context.Project(normalized);
        Assert.NotEqual(firstAlias, fresh["matches"]![0]!["id"]!.GetValue<string>());
        Assert.False(context.TryResolveSelectors(new JsonObject { ["nodeId"] = firstAlias }, out var error));
        Assert.Contains("expired", error);
    }

    [Fact]
    public void NestedHierarchy_PreservesDistinctIdenticalAncestorsAndLeavesSourceUnchanged()
    {
        var tree = JsonNode.Parse("""
            {"format":"ansight.maui.visual-tree.compact.v2","types":["ContentPage","Border","Label"],
             "flagBits":{"visible":1,"enabled":2},"root":{"id":"page","typeId":0,"flags":3,
             "automationId":"page","children":[
               {"id":"container-a","typeId":1,"flags":3,"automationId":"repeated-container","children":[
                 {"id":"label-a","typeId":2,"flags":3,"label":"Repeated label"}]},
               {"id":"container-b","typeId":1,"flags":3,"automationId":"repeated-container","children":[
                 {"id":"label-b","typeId":2,"flags":3,"label":"Repeated label"}]}
             ]}}
            """)!.AsObject();
        var before = tree.ToJsonString();
        var nested = VisualTreeObservation.Build(tree, "session", "app", "maui.get_visual_tree")!;
        Assert.Equal(before, tree.ToJsonString());
        Assert.Null(nested["nodes"]);
        Assert.DoesNotContain("ancestorPath", nested.ToJsonString());
        var containers = Assert.IsType<JsonArray>(nested["root"]!["children"]);
        Assert.Equal(2, containers.Count);
        Assert.Equal("container-a", containers[0]!["id"]!.GetValue<string>());
        Assert.Equal("container-b", containers[1]!["id"]!.GetValue<string>());
        Assert.Equal("label-a", containers[0]!["children"]![0]!["id"]!.GetValue<string>());
        Assert.Equal("label-b", containers[1]!["children"]![0]!["id"]!.GetValue<string>());
        Assert.All(containers.OfType<JsonObject>(), container =>
        {
            Assert.Equal("repeated-container", container["automationId"]!.GetValue<string>());
            Assert.Equal("Repeated label", container["children"]![0]!["text"]!.GetValue<string>());
        });
        Assert.Equal(4, nested["returnedNodeCount"]!.GetValue<int>());
    }
}
