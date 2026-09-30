using System.Text.Json.Nodes;

namespace Ansight.Host.Tests.Unit.Runtime;

public sealed class LiveUiTapHintTests
{
    [Fact]
    public void BuildTapHint_ReplaysResultLabelWithoutTheTextFieldIndex()
    {
        var root = SearchTree();
        var registry = CreateRegistry();
        var query = LiveUiSelector.Parse(new JsonObject { ["text"] = "Kalymnos", ["visible"] = true });
        var matches = LiveUiNodeQuery.Find(root, query, registry);
        Assert.Equal(2, matches.Count);

        var hint = FindLiveUiTool.BuildTapHint(root, matches[1], new LiveUiBounds(0, 0, 400, 850), query);
        var selector = Assert.IsType<JsonObject>(hint?["selector"]);
        Assert.Equal("text", selector["role"]!.GetValue<string>());
        Assert.Equal("StaticText", selector["type"]!.GetValue<string>());
        Assert.False(selector.ContainsKey("index"));
        Assert.Same(matches[1].Node, Assert.Single(LiveUiNodeQuery.Find(root, LiveUiSelector.Parse(selector), registry)).Node);

        var compact = UiResultProjection.Project(new JsonObject
        {
            ["matches"] = new JsonArray(new JsonObject { ["text"] = "Kalymnos", ["tapHint"] = hint }),
            ["totalMatches"] = 1
        }, UiProjectionOptions.Model);
        Assert.True(JsonNode.DeepEquals(selector, compact["matches"]![0]!["tapHint"]!["selector"]));
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("disabled")]
    [InlineData("offscreen")]
    [InlineData("hidden")]
    public void BuildTapHint_DoesNotInventAmbiguousOrUnavailableTargets(string state)
    {
        var root = SearchTree();
        var label = root["children"]![1]!;
        if (state == "duplicate")
        {
            root["children"]!.AsArray().Add(label.DeepClone());
        }
        else if (state == "offscreen")
        {
            label["bounds"]!["y"] = 900;
        }
        else
        {
            label[state == "disabled" ? "enabled" : "visible"] = false;
        }
        var registry = CreateRegistry();
        var query = LiveUiSelector.Parse(new JsonObject { ["text"] = "Kalymnos", ["role"] = "text" });
        var match = LiveUiNodeQuery.Find(root, query, registry)[0];

        Assert.Null(FindLiveUiTool.BuildTapHint(root, match, new LiveUiBounds(0, 0, 400, 850), query));
    }

    private static JsonObject SearchTree()
        => new()
        {
            ["typeId"] = 0,
            ["visible"] = true,
            ["enabled"] = true,
            ["children"] = new JsonArray(Node("TextField", "textbox", 50), Node("StaticText", "text", 300))
        };

    [Fact]
    public void CompactObservation_DoesNotOverrideResolverRefusalWithAnAncestorGuess()
    {
        var result = UiResultProjection.Project(new JsonObject
        {
            ["matches"] = new JsonArray(new JsonObject
            {
                ["text"] = "Kalymnos",
                ["tapHint"] = null,
                ["ancestorPath"] = new JsonArray(new JsonObject { ["automationId"] = "results-panel" })
            }),
            ["totalMatches"] = 1
        }, UiProjectionOptions.Model);

        Assert.Null(result["matches"]![0]!["tapHint"]);
    }

    private static JsonObject Node(string type, string role, int y)
        => new()
        {
            ["typeId"] = type == "TextField" ? 1 : 2,
            ["role"] = role,
            ["text"] = "Kalymnos",
            ["visible"] = true,
            ["enabled"] = true,
            ["supportedActions"] = role == "textbox" ? new JsonArray("tap", "typeText") : new JsonArray(),
            ["bounds"] = new JsonObject { ["x"] = 20, ["y"] = y, ["width"] = 300, ["height"] = 40 }
        };

    private static VisualTreeTypeRegistry CreateRegistry()
        => VisualTreeTypeRegistry.FromPayload(new JsonObject
        {
            ["types"] = new JsonArray("Application", "TextField", "StaticText")
        });
}
