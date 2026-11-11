using System.Text.Json.Nodes;

namespace Ansight.Host.Tests.Unit.Runtime;

public sealed class AvaloniaContainerTargetTests
{
    [Theory]
    [InlineData("pane")]
    [InlineData("group")]
    public void InheritedContainerLabelDoesNotSupplyATapTarget(string role)
    {
        var root = Container(role);
        var registry = VisualTreeTypeRegistry.FromPayload(new JsonObject());
        var selector = LiveUiSelector.Parse(new JsonObject { ["text"] = "Buttons" });
        var selection = FindLiveUiTool.FindSemanticMatches(root, registry, selector);
        var match = Assert.Single(selection.StrictMatches);

        Assert.Null(FindLiveUiTool.BuildTapHint(root, match, new LiveUiBounds(0, 0, 390, 844), selector));
        Assert.True(FindLiveUiTool.ShouldSearchScreenshotText(selection, selector));
    }

    [Fact]
    public void ExplicitContainerIdentityRemainsQueryableWithoutOcrSubstitution()
    {
        var root = Container("pane");
        var registry = VisualTreeTypeRegistry.FromPayload(new JsonObject());
        var selector = LiveUiSelector.Parse(new JsonObject { ["automationId"] = "NavPage" });
        var selection = FindLiveUiTool.FindSemanticMatches(root, registry, selector);

        Assert.Single(selection.StrictMatches);
        Assert.False(FindLiveUiTool.ShouldSearchScreenshotText(selection, selector));
    }

    [Fact]
    public void ActionablePaneRetainsItsObservedTapTarget()
    {
        var root = Container("pane");
        root["supportedActions"] = new JsonArray("tap");
        var registry = VisualTreeTypeRegistry.FromPayload(new JsonObject());
        var selector = LiveUiSelector.Parse(new JsonObject { ["text"] = "Buttons" });
        var selection = FindLiveUiTool.FindSemanticMatches(root, registry, selector);
        var match = Assert.Single(selection.StrictMatches);

        Assert.NotNull(FindLiveUiTool.BuildTapHint(root, match, new LiveUiBounds(0, 0, 390, 844), selector));
        Assert.False(FindLiveUiTool.ShouldSearchScreenshotText(selection, selector));
    }

    [Fact]
    public void PositionedTextChildPreventsUnnecessaryScreenshotSearch()
    {
        var root = Container("pane");
        var child = Container("text");
        child["automationId"] = "PageHeading";
        child["bounds"] = new JsonObject { ["x"] = 30, ["y"] = 60, ["width"] = 80, ["height"] = 20 };
        root["children"] = new JsonArray(child);
        var registry = VisualTreeTypeRegistry.FromPayload(new JsonObject());
        var selector = LiveUiSelector.Parse(new JsonObject { ["text"] = "Buttons" });
        var selection = FindLiveUiTool.FindSemanticMatches(root, registry, selector);

        Assert.Equal(2, selection.StrictMatches.Count);
        Assert.False(FindLiveUiTool.ShouldSearchScreenshotText(selection, selector));
    }

    private static JsonObject Container(string role)
    {
        return new JsonObject
        {
            ["type"] = "Avalonia.Controls.NavigationPage",
            ["automationId"] = "NavPage",
            ["label"] = "Buttons",
            ["role"] = role,
            ["visible"] = true,
            ["enabled"] = true,
            ["supportedActions"] = new JsonArray("bring_into_view"),
            ["bounds"] = new JsonObject { ["x"] = 0, ["y"] = 1, ["width"] = 390, ["height"] = 843 }
        };
    }
}
