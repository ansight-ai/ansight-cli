using System.Text.Json.Nodes;

namespace Ansight.Host.Tests.Unit.Runtime;

public sealed class UiObservationEfficiencyTests
{
    [Fact]
    public void DeepNativeTreeKeepsLateBottomNavigationWithinTheModelBudget()
    {
        var children = new JsonArray(Enumerable.Range(0, 1500).Select(index => (JsonNode?)new JsonObject
        {
            ["id"] = "content-" + index, ["typeId"] = 1, ["label"] = new string('x', 100),
            ["automationId"] = "control-" + index, ["visible"] = true, ["enabled"] = true,
            ["supportedActions"] = new JsonArray("tap"), ["bounds"] = new JsonArray(0, 100, 200, 40)
        }).ToArray());
        foreach (var label in new[] { "Explore", "Map", "Profile" })
            children.Add(new JsonObject
            {
                ["id"] = "tab-" + label, ["automationId"] = "tab-" + label, ["label"] = label,
                ["typeId"] = 2, ["role"] = "group", ["visible"] = true, ["enabled"] = true,
                ["supportedActions"] = new JsonArray("tap"), ["bounds"] = new JsonArray(0, 800, 100, 40)
            });
        var source = new JsonObject
        {
            ["format"] = "ansight.native.visual-tree.compact.v2", ["platform"] = "android",
            ["types"] = new JsonArray("android.view.WindowRoots", "FrameLayout", "BottomNavigationItemView"),
            ["root"] = new JsonObject { ["id"] = "root", ["typeId"] = 0, ["visible"] = true, ["enabled"] = true,
                ["bounds"] = new JsonArray(0, 0, 390, 844), ["children"] = children }
        };
        var observation = Assert.IsType<JsonObject>(VisualTreeObservation.Build(source, "session", "app", "ui.get_visual_tree"));
        Assert.InRange(observation.ToJsonString().Length, 1, 8000);
        foreach (var label in new[] { "Explore", "Map", "Profile" })
            Assert.Contains("tab-" + label, observation.ToJsonString());
        Assert.True(observation["truncated"]!.GetValue<bool>());
        var actionableNodes = Descendants(observation).OfType<JsonObject>()
            .Where(node => node["tapHint"] is JsonObject).ToArray();
        foreach (var label in new[] { "Explore", "Map", "Profile" })
        {
            var hint = Assert.Single(actionableNodes, node => node["automationId"]?.GetValue<string>() == "tab-" + label)["tapHint"]!;
            Assert.Equal("ui.get_visual_tree", hint["selector"]!["targetSource"]!.GetValue<string>());
            Assert.False(string.IsNullOrWhiteSpace(hint["selector"]!["targetFingerprint"]!.GetValue<string>()));
        }
    }

    private static IEnumerable<JsonNode> Descendants(JsonNode node)
    {
        yield return node;
        IEnumerable<JsonNode?> children = node is JsonObject obj ? obj.Select(entry => entry.Value)
            : node is JsonArray array ? array : [];
        foreach (var child in children.Where(child => child is not null))
            foreach (var descendant in Descendants(child!)) yield return descendant;
    }

    [Fact]
    public void CompleteEnvelopeBudgetPreservesNavigationVerdictAndExactTarget()
    {
        var children = new JsonArray(Enumerable.Range(0, 60).Select(index => (JsonNode?)new JsonObject
        {
            ["id"] = "item-" + index, ["text"] = new string('x', 160), ["onScreen"] = index > 50,
            ["children"] = new JsonArray(new JsonObject { ["id"] = "child-" + index, ["text"] = new string('y', 120) })
        }).ToArray());
        children.Add(new JsonObject { ["id"] = "map-tab", ["automationId"] = "map-tab-exact", ["role"] = "tab", ["selected"] = true, ["onScreen"] = true });
        var source = new JsonObject
        {
            ["performed"] = true, ["satisfied"] = true,
            ["target"] = new JsonObject { ["automationId"] = "unmodified-exact-selector", ["role"] = "button" },
            ["afterObservation"] = new JsonObject
            {
                ["capability"] = "ui.observe", ["nodeCount"] = 122, ["returnedNodeCount"] = 121,
                ["root"] = new JsonObject { ["id"] = "root", ["children"] = children },
                ["structureNodes"] = new JsonArray(new JsonObject { ["id"] = "map-tab", ["parentId"] = "root", ["role"] = "tab" })
            }
        };
        var original = source.ToJsonString();
        var projected = UiResultProjection.Project(source, UiProjectionOptions.Model with { MaximumCharacters = 3000, MaximumNodes = 15 });
        Assert.True(projected.ToJsonString().Length <= 3000);
        Assert.True(projected["performed"]!.GetValue<bool>());
        Assert.True(projected["satisfied"]!.GetValue<bool>());
        Assert.Equal("unmodified-exact-selector", projected["target"]!["automationId"]!.GetValue<string>());
        Assert.Contains("map-tab-exact", projected.ToJsonString());
        Assert.True(projected["afterObservation"]!["truncated"]!.GetValue<bool>());
        Assert.Equal(original, source.ToJsonString());
    }

    [Fact]
    public void NavigationFingerprintIgnoresTypingButDetectsTabPageAndModalChanges()
    {
        var before = NavigationTree();
        var typed = before.DeepClone().AsObject();
        typed["root"]!["children"]![0]!["value"] = "new query";
        var fingerprint = UiNavigationContext.Fingerprint(before);
        Assert.NotNull(fingerprint);
        Assert.Equal(fingerprint, UiNavigationContext.Fingerprint(typed));
        var navigated = before.DeepClone().AsObject();
        navigated["currentPage"]!["id"] = "other-page";
        Assert.NotEqual(fingerprint, UiNavigationContext.Fingerprint(navigated));
        var tab = before.DeepClone().AsObject();
        tab["root"]!["children"]![1]!["selected"] = false;
        Assert.NotEqual(fingerprint, UiNavigationContext.Fingerprint(tab));
        var modal = before.DeepClone().AsObject();
        modal["root"]!["children"]!.AsArray().Add(new JsonObject { ["id"] = "dialog", ["typeId"] = 0, ["role"] = "dialog", ["flags"] = 3 });
        Assert.NotEqual(fingerprint, UiNavigationContext.Fingerprint(modal));
    }

    [Fact]
    public void NavigationFingerprintRequiresCompleteWholeRootAndActivePageEvidence()
    {
        var tree = NavigationTree();
        tree["truncated"] = true;
        Assert.Null(UiNavigationContext.Fingerprint(tree));
        tree.Remove("truncated");
        tree["rootScope"] = "currentPage";
        Assert.Null(UiNavigationContext.Fingerprint(tree));
        tree["rootScope"] = "rootPage";
        tree.Remove("currentPage");
        Assert.Null(UiNavigationContext.Fingerprint(tree));
    }

    private static JsonObject NavigationTree() => new()
    {
        ["format"] = "ansight.maui.visual-tree.compact.v2", ["rootScope"] = "rootPage",
        ["currentPage"] = new JsonObject { ["id"] = "page" },
        ["types"] = new JsonArray("ContentPage", "Entry", "Tab"),
        ["flagBits"] = new JsonObject { ["visible"] = 1, ["enabled"] = 2 },
        ["root"] = new JsonObject
        {
            ["id"] = "page", ["typeId"] = 0, ["flags"] = 3,
            ["children"] = new JsonArray(
                new JsonObject { ["id"] = "search", ["typeId"] = 1, ["flags"] = 3 },
                new JsonObject { ["id"] = "tab", ["typeId"] = 2, ["role"] = "tab", ["selected"] = true, ["flags"] = 3 })
        }
    };
}
