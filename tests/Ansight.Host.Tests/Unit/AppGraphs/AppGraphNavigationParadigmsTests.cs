using Ansight.Host.AppGraphs;
using Ansight.Host.Runtime.Operations.Tools.UiAutomation;

namespace Ansight.Host.Tests.Unit.AppGraphs;

public sealed class AppGraphNavigationParadigmsTests
{
    [Theory]
    [InlineData("ios-uikit", "ui_navigation_controller", false)]
    [InlineData("maui", "navigation_page", false)]
    [InlineData("maui", "modal_stack", false)]
    [InlineData(" IOS-UIKIT ", " UI_NAVIGATION_CONTROLLER ", false)]
    [InlineData("react-native", "react_navigation_stack", true)]
    [InlineData("ios-swiftui", "navigation_split_view", true)]
    [InlineData("flutter", "bottom_navigation_bar", true)]
    public void ValidationUsesTechnologyOrderRatherThanCompatibilityRole(string framework, string kind, bool firstChildCanBeActive)
    {
        var technology = new AppGraphNavigationTechnologyDescriptor(framework, kind, "", "");
        Assert.Equal(firstChildCanBeActive,
            AppGraphNavigationTechnologyCatalog.TryValidateChildOrder(technology, ["root", "next"], "root", out _));
        Assert.True(AppGraphNavigationTechnologyCatalog.TryValidateChildOrder(technology, ["root", "next"], "next", out _));
        Assert.True(AppGraphNavigationTechnologyCatalog.TryValidateChildOrder(technology, ["root"], "root", out _));
    }

    [Fact]
    public void EveryRegisteredTechnologyExposesItsParadigmAndEvidence()
    {
        foreach (var framework in AppGraphNavigationTechnologyCatalog.Frameworks)
        {
            var published = AppGraphNavigationTechnologyCatalog.BuildKindsJson(framework.Framework);
            Assert.Equal(framework.Kinds.Count, published.Count);
            Assert.All(published, kind =>
            {
                Assert.NotNull(kind!["paradigm"]);
                Assert.False(string.IsNullOrWhiteSpace(kind["paradigm"]!["childInventory"]!.GetValue<string>()));
                Assert.False(string.IsNullOrWhiteSpace(kind["paradigm"]!["evidence"]!.GetValue<string>()));
            });
        }
    }

    [Fact]
    public void ComposedGuidanceRetainsExactFrameworkParadigmsWithoutOtherToolkits()
    {
        var react = NavigationGuidance.Read("react-native");
        Assert.Contains("routes[].key", react);
        Assert.Contains("index is authoritative", react);
        Assert.DoesNotContain("FlyoutPage", react);
        Assert.DoesNotContain("UINavigationController", react);

        var swift = NavigationGuidance.Read("ios-swiftui");
        Assert.Contains("adaptive_columns", swift);
        Assert.Contains("focus, not exclusive visibility", swift);
        Assert.Contains("NavigationPath", swift);
        Assert.DoesNotContain("ShellItem", swift);
    }
}
