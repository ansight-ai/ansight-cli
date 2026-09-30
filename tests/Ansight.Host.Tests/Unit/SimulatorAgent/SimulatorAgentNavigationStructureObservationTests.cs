using System.Text.Json.Nodes;
using Ansight.Host.Runtime.Operations;

namespace Ansight.Host.Tests.Unit.SimulatorAgent;

public sealed class SimulatorAgentNavigationStructureObservationTests
{
    [Fact]
    public void Build_IdentifiesReactNativeControllerAndReturnsStableFingerprint()
    {
        var content = new JsonObject
        {
            ["sessionId"] = "session-1",
            ["appId"] = "com.example.app",
            ["toolId"] = "react.get_navigation_state",
            ["payload"] = new JsonObject
            {
                ["result"] = new JsonObject
                {
                    ["capturedAtUtc"] = "2026-08-28T01:00:00Z",
                    ["type"] = "tab",
                    ["index"] = 0,
                    ["routes"] = new JsonArray
                    {
                        new JsonObject { ["key"] = "home-1", ["name"] = "Home" },
                        new JsonObject { ["key"] = "profile-1", ["name"] = "Profile" }
                    }
                }
            }
        };

        var first = Assert.IsType<JsonObject>(
            NavigationStructureObservation.Build(content));
        content["payload"]!["result"]!["capturedAtUtc"] = "2026-08-28T01:01:00Z";
        var second = Assert.IsType<JsonObject>(
            NavigationStructureObservation.Build(content));

        Assert.Equal("react-native", first["framework"]?.GetValue<string>());
        Assert.Equal(
            first["structureFingerprint"]?.GetValue<string>(),
            second["structureFingerprint"]?.GetValue<string>());
        Assert.Null(first["guidance"]);
        var controller = Assert.IsType<JsonObject>(Assert.IsType<JsonArray>(first["availableNavigationControllers"])[0]);
        Assert.Equal("react-native", controller["framework"]?.GetValue<string>());
        Assert.Equal("react.get_navigation_state", controller["navigationToolId"]?.GetValue<string>());
        Assert.Null(controller["guidance"]);
        Assert.NotEmpty(Assert.IsType<JsonArray>(controller["technologyKinds"]));
        Assert.Null(first["state"]?["capturedAtUtc"]);
        Assert.Equal("Home", first["state"]?["routes"]?[0]?["name"]?.GetValue<string>());
    }

    [Fact]
    public void NavigationToolCatalog_SelectsExactComposeController()
    {
        var catalog = new JsonObject
        {
            ["tools"] = new JsonArray
            {
                new JsonObject { ["id"] = "ui.get_visual_tree" },
                new JsonObject { ["id"] = "android.compose.get_navigation_state" }
            }
        };

        var resolved = GetLiveNavigationStructureTool.TryResolveNavigationTool(
            catalog,
            framework: null,
            out var controller,
            out var toolId);

        Assert.True(resolved);
        Assert.Equal("android-compose", controller?.Framework);
        Assert.Equal("android.compose.get_navigation_state", toolId);
    }

    [Fact]
    public void NavigationToolCatalog_HonorsMixedPlatformFrameworkOverride()
    {
        var catalog = new JsonObject
        {
            ["tools"] = new JsonArray
            {
                new JsonObject { ["id"] = "ios.uikit.get_navigation_state" },
                new JsonObject { ["id"] = "ios.swiftui.get_navigation_state" }
            }
        };

        var resolved = GetLiveNavigationStructureTool.TryResolveNavigationTool(
            catalog,
            "maccatalyst-swiftui",
            out var controller,
            out var toolId);

        Assert.True(resolved);
        Assert.Equal("maccatalyst-swiftui", controller?.Framework);
        Assert.Equal("ios.swiftui.get_navigation_state", toolId);
    }

    [Fact]
    public void NavigationToolCatalog_ReportsEveryDistinctMixedToolkitController()
    {
        var catalog = new JsonObject
        {
            ["tools"] = new JsonArray
            {
                new JsonObject { ["id"] = "ios.uikit.get_navigation_state" },
                new JsonObject { ["id"] = "ios.swiftui.get_navigation_state" }
            }
        };

        var controllers = GetLiveNavigationStructureTool.BuildAvailableNavigationControllers(
            catalog,
            "ios-uikit");

        Assert.Collection(
            controllers.OfType<JsonObject>(),
            controller =>
            {
                Assert.Equal("ios-uikit", controller["framework"]?.GetValue<string>());
                Assert.Contains("UITabBarController", controller["guidance"]?.GetValue<string>(), StringComparison.Ordinal);
                Assert.Contains(
                    Assert.IsType<JsonArray>(controller["technologyKinds"]).OfType<JsonObject>(),
                    kind => kind["kind"]?.GetValue<string>() == "ui_tab_bar_controller");
            },
            controller =>
            {
                Assert.Equal("ios-swiftui", controller["framework"]?.GetValue<string>());
                Assert.Contains("NavigationStack", controller["guidance"]?.GetValue<string>(), StringComparison.Ordinal);
                Assert.Contains(
                    Assert.IsType<JsonArray>(controller["technologyKinds"]).OfType<JsonObject>(),
                    kind => kind["kind"]?.GetValue<string>() == "navigation_stack");
            });
    }

    [Fact]
    public void PrepareStructuredContent_ClonesParentedToolPayloadBeforeRewrapping()
    {
        var catalog = new JsonObject
        {
            ["tools"] = new JsonArray
            {
                new JsonObject { ["id"] = "maui.get_navigation_state" }
            }
        };
        Assert.True(GetLiveNavigationStructureTool.TryResolveNavigationTool(
            catalog,
            "maui",
            out var controller,
            out _));
        var structuredContent = new JsonObject
        {
            ["sessionId"] = "session-1",
            ["appId"] = "com.example.app",
            ["payload"] = new JsonObject
            {
                ["result"] = new JsonObject
                {
                    ["currentPage"] = "Map"
                }
            }
        };
        var originalEnvelope = new JsonObject
        {
            ["structuredContent"] = structuredContent
        };

        var navigationContent = GetLiveNavigationStructureTool.PrepareStructuredContent(
            structuredContent,
            catalog,
            controller);
        var result = RequestResult.ToolResult(navigationContent, isError: false);

        Assert.Same(originalEnvelope, structuredContent.Parent);
        Assert.NotSame(structuredContent, navigationContent);
        Assert.Equal("maui", navigationContent["selectedNavigationController"]?.GetValue<string>());
        Assert.Equal(
            "maui",
            navigationContent["availableNavigationControllers"]?[0]?["framework"]?.GetValue<string>());
        Assert.Same(navigationContent, result.Payload?["structuredContent"]);
    }

    [Fact]
    public void Build_HonorsSelectedControllerWhenPlatformsShareAToolId()
    {
        var content = new JsonObject
        {
            ["toolId"] = "ios.swiftui.get_navigation_state",
            ["selectedNavigationController"] = "maccatalyst-swiftui",
            ["payload"] = new JsonObject
            {
                ["result"] = new JsonObject
                {
                    ["path"] = new JsonArray("Home", "Detail")
                }
            }
        };

        var observation = Assert.IsType<JsonObject>(
            NavigationStructureObservation.Build(content));

        Assert.Equal("maccatalyst-swiftui", observation["framework"]?.GetValue<string>());
        Assert.Equal("maccatalyst-swiftui", observation["selectedNavigationController"]?.GetValue<string>());
        Assert.Equal("ios.swiftui.get_navigation_state", observation["navigationToolId"]?.GetValue<string>());
        Assert.Null(observation["guidance"]);
    }

    [Fact]
    public void Build_RemovesOnlyControllerGuidanceAndPreservesSourceAndNavigationState()
    {
        var content = new JsonObject
        {
            ["toolId"] = "ios.uikit.get_navigation_state",
            ["selectedNavigationController"] = "ios-uikit",
            ["availableNavigationControllers"] = new JsonArray
            {
                new JsonObject
                {
                    ["framework"] = "ios-uikit",
                    ["navigationToolId"] = "ios.uikit.get_navigation_state",
                    ["available"] = true,
                    ["guidance"] = "Repeated UIKit instructions",
                    ["technologyKinds"] = new JsonArray
                    {
                        new JsonObject { ["kind"] = "ui_tab_bar_controller", ["supportsTabGroup"] = true }
                    }
                },
                new JsonObject
                {
                    ["framework"] = "ios-swiftui",
                    ["navigationToolId"] = "ios.swiftui.get_navigation_state",
                    ["available"] = false,
                    ["guidance"] = "Repeated SwiftUI instructions",
                    ["technologyKinds"] = new JsonArray
                    {
                        new JsonObject { ["kind"] = "navigation_stack", ["supportsNavigationHost"] = true }
                    }
                }
            },
            ["payload"] = new JsonObject
            {
                ["result"] = new JsonObject
                {
                    ["selectedIndex"] = 1,
                    ["guidance"] = "App navigation destination content",
                    ["routes"] = new JsonArray("Home", "Guidance")
                }
            }
        };
        var original = content.ToJsonString();

        var observation = Assert.IsType<JsonObject>(NavigationStructureObservation.Build(content));

        Assert.Equal(original, content.ToJsonString());
        Assert.Null(observation["guidance"]);
        var controllers = Assert.IsType<JsonArray>(observation["availableNavigationControllers"]);
        Assert.Equal(2, controllers.Count);
        for (var index = 0; index < controllers.Count; index++)
        {
            var expected = Assert.IsType<JsonObject>(content["availableNavigationControllers"]![index]!.DeepClone());
            expected.Remove("guidance");
            Assert.True(JsonNode.DeepEquals(expected, controllers[index]));
            Assert.Null(controllers[index]!["guidance"]);
        }
        Assert.True(JsonNode.DeepEquals(content["payload"]!["result"], observation["state"]));

        content["availableNavigationControllers"]![0]!["guidance"] = "Updated instructions";
        var updated = Assert.IsType<JsonObject>(NavigationStructureObservation.Build(content));
        Assert.Equal(observation["structureFingerprint"]!.GetValue<string>(), updated["structureFingerprint"]!.GetValue<string>());
    }
}
