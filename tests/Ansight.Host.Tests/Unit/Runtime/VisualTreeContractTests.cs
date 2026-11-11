namespace Ansight.Host.Tests.Unit.Runtime;

public sealed class VisualTreeContractTests
{
    [Theory]
    [InlineData("maui.get_visual_tree", null, null, null, "maui")]
    [InlineData("avalonia.get_visual_tree", null, null, null, "avalonia-visual")]
    [InlineData("avalonia.get_logical_tree", null, null, null, "avalonia-logical")]
    [InlineData("avalonia.get_accessibility_tree", null, null, null, "avalonia-accessibility")]
    [InlineData(null, "unknown", "avalonia-visual", "ansight.visual-tree.compact.v2", "avalonia-visual")]
    [InlineData(null, "unknown", "avalonia-logical", "ansight.visual-tree.compact.v2", "avalonia-logical")]
    [InlineData(null, "unknown", "avalonia-accessibility", "ansight.visual-tree.compact.v2", "avalonia-accessibility")]
    [InlineData(null, null, null, "ansight.avalonia.accessibility-tree.compact.v2", "avalonia-accessibility")]
    [InlineData("ui.get_visual_tree", null, "avalonia-logical", null, "avalonia-logical")]
    [InlineData(null, "logical", null, "ansight.avalonia.visual-tree.compact.v2", "avalonia-logical")]
    [InlineData(null, "visual", null, "ansight.avalonia.visual-tree.compact.v2", "avalonia-visual")]
    [InlineData("react.get_component_tree", null, null, null, "react-component")]
    [InlineData("react.get_shadow_tree", null, null, null, "react-shadow")]
    [InlineData("flutter.get_widget_tree", null, null, null, "flutter")]
    [InlineData("dom.get_document", null, null, null, "dom")]
    [InlineData("ui.get_visual_tree", null, null, null, "native")]
    [InlineData(null, null, "core-simulator-ax-service", null, "accessibility")]
    [InlineData(null, "unknown", "core-simulator-ax-service", "ansight.device-accessibility.compact.v2", "accessibility")]
    [InlineData(null, null, "device-accessibility", null, "accessibility")]
    [InlineData(null, null, null, "ansight.device-accessibility.compact.v2", "accessibility")]
    [InlineData(null, "component", "react", null, "react-component")]
    [InlineData(null, "shadow", "react-native", null, "react-shadow")]
    [InlineData(null, null, null, "ansight.native.visual-tree.compact.v2", "native")]
    [InlineData(null, "custom", "custom", "custom", "unknown")]
    public void NormalizeKind_ReturnsCanonicalWireValue(
        string? toolId,
        string? reportedKind,
        string? source,
        string? format,
        string expected)
    {
        var actual = VisualTreeContract.NormalizeKind(toolId, reportedKind, source, format);

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("ansight.device-accessibility.compact.v2", "ansight.device-accessibility.compact.v2")]
    [InlineData("ansight.dom.visual-tree.compact.v2", "ansight.dom.visual-tree.compact.v2")]
    [InlineData("ansight.avalonia.visual-tree.compact.v2", "ansight.avalonia.visual-tree.compact.v2")]
    [InlineData("ANSIGHT.MAUI.VISUAL-TREE.COMPACT.V2", "ansight.maui.visual-tree.compact.v2")]
    [InlineData(null, "ansight.visual-tree.compact.v2")]
    [InlineData("custom", "ansight.visual-tree.compact.v2")]
    public void NormalizeFormat_ReturnsCanonicalWireValue(string? value, string expected)
    {
        Assert.Equal(expected, VisualTreeContract.NormalizeFormat(value));
    }

    [Theory]
    [InlineData("Android", "android")]
    [InlineData("android-native", "android")]
    [InlineData(".NET", "dotnet")]
    [InlineData("iPad", "ios")]
    [InlineData("ios-native", "ios")]
    [InlineData("Mac-Catalyst", "maccatalyst")]
    [InlineData("OSX", "macos")]
    [InlineData(null, "unknown")]
    [InlineData("custom", "unknown")]
    public void NormalizeRuntimePlatform_ReturnsCanonicalWireValue(string? value, string expected)
    {
        Assert.Equal(expected, VisualTreeContract.NormalizeRuntimePlatform(value));
    }

    [Theory]
    [InlineData("CURRENT_PAGE", null, "currentPage")]
    [InlineData("root-page", null, "rootPage")]
    [InlineData("window", null, "window")]
    [InlineData("root", null, "root")]
    [InlineData(null, "maui.get_visual_tree", "currentPage")]
    [InlineData(null, "ui.get_visual_tree", "root")]
    public void NormalizeRootScope_ReturnsCanonicalWireValue(
        string? value,
        string? toolId,
        string expected)
    {
        Assert.Equal(expected, VisualTreeContract.NormalizeRootScope(value, toolId));
    }
}
