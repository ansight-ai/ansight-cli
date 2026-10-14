using System.Text.Json.Nodes;

namespace Ansight.Host.Tests.Unit.Runtime;

public sealed class UiObservationEfficiencyTests
{
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
