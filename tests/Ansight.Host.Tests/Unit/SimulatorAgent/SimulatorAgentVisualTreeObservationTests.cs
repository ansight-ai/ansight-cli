using System.Text.Json.Nodes;

namespace Ansight.Host.Tests.Unit.SimulatorAgent;

public sealed class SimulatorAgentVisualTreeObservationTests
{
    [Fact]
    public void Build_BudgetsNestedNodesWithoutChargingForRepeatedSharedAncestorPaths()
    {
        var root = new JsonObject
        {
            ["id"] = "page", ["typeId"] = 0, ["automationId"] = "AccountPage",
            ["flags"] = 3, ["bounds"] = CompactBounds(0, 0, 390, 844), ["children"] = new JsonArray()
        };
        var parent = root;
        for (var index = 0; index < 3; index++)
        {
            var section = new JsonObject
            {
                ["id"] = $"section-{index}", ["typeId"] = 1,
                ["automationId"] = $"account-section-{index}", ["label"] = new string((char)('a' + index), 100),
                ["flags"] = 3, ["bounds"] = CompactBounds(0, 0, 390, 844), ["children"] = new JsonArray()
            };
            parent["children"]!.AsArray().Add(section);
            parent = section;
        }
        for (var index = 0; index < 10; index++)
        {
            parent["children"]!.AsArray().Add(new JsonObject
            {
                ["id"] = $"control-{index}", ["typeId"] = 2,
                ["automationId"] = $"account-control-{index}",
                ["label"] = index == 9 ? "Roles and Permissions" : $"Control {index}",
                ["flags"] = 3, ["bounds"] = CompactBounds(10, 30 * index, 200, 25), ["children"] = new JsonArray()
            });
        }
        var tree = new JsonObject
        {
            ["format"] = "ansight.maui.visual-tree.compact.v2",
            ["types"] = new JsonArray("ContentPage", "Grid", "Button"),
            ["flagBits"] = new JsonObject { ["visible"] = 1, ["enabled"] = 2 },
            ["coordinateSpace"] = Bounds(0, 0, 390, 844), ["root"] = root
        };

        var observation = Assert.IsType<JsonObject>(VisualTreeObservation.Build(
            tree, "session", "app", "maui.get_visual_tree",
            UiProjectionOptions.Model with { MaximumCharacters = 3500 }));

        Assert.Equal(13, observation["returnedNodeCount"]!.GetValue<int>());
        Assert.False(observation["truncated"]!.GetValue<bool>());
        var nodes = Descendants(observation["root"]!.AsObject()).ToArray();
        Assert.Contains(nodes, node => node["text"]?.GetValue<string>() == "Roles and Permissions");
        Assert.All(nodes, node =>
        {
            Assert.False(node.ContainsKey("ancestorPath"));
            Assert.False(node.ContainsKey("ancestorPathTruncated"));
            Assert.False(node.ContainsKey("projectionTruncated"));
        });
    }

    [Fact]
    public void Build_ReturnsBoundedCompactDigestWithoutRawNodePayloads()
    {
        var children = new JsonArray();
        for (var index = 0; index < 100; index++)
        {
            children.Add(new JsonObject
            {
                ["id"] = $"node-{index}",
                ["typeId"] = 1,
                ["automationId"] = $"button-{index}",
                ["label"] = $"Button {index}",
                ["flags"] = 3,
                ["bounds"] = CompactBounds(10, index * 20, 100, 18),
                ["properties"] = new JsonObject
                {
                    ["largeDebugPayload"] = new string('x', 2_000)
                },
                ["children"] = new JsonArray()
            });
        }

        var visualTree = new JsonObject
        {
            ["format"] = "ansight.maui.visual-tree.compact.v2",
            ["types"] = new JsonArray("ContentPage", "Button"),
            ["flagBits"] = new JsonObject
            {
                ["visible"] = 1,
                ["enabled"] = 2
            },
            ["coordinateSpace"] = Bounds(0, 0, 393, 852),
            ["root"] = new JsonObject
            {
                ["id"] = "root",
                ["typeId"] = 0,
                ["automationId"] = "home-page",
                ["label"] = "Home",
                ["flags"] = 3,
                ["bounds"] = CompactBounds(0, 0, 393, 852),
                ["children"] = children
            }
        };

        var original = visualTree.ToJsonString();
        var observation = VisualTreeObservation.Build(
            visualTree,
            "session-001",
            "com.example.app",
            "maui.get_visual_tree");

        Assert.NotNull(observation);
        Assert.Equal(original, visualTree.ToJsonString());
        Assert.Null(observation["nodes"]);
        var nodes = Descendants(Assert.IsType<JsonObject>(observation["root"])).ToArray();
        Assert.InRange(nodes.Length, 1, 40);
        Assert.Equal(nodes.Length, observation["returnedNodeCount"]?.GetValue<int>());
        Assert.True(observation["truncated"]?.GetValue<bool>());
        Assert.DoesNotContain("ancestorPath", observation.ToJsonString());
        Assert.All(nodes, node =>
        {
            Assert.False(node.ContainsKey("properties"));
            Assert.False(node.ContainsKey("flags"));
            Assert.False(node.ContainsKey("typeId"));
            Assert.Equal(4, Assert.IsType<JsonArray>(node["bounds"]).Count);
        });
        Assert.True(observation.ToJsonString().Length < 12_000);
    }

    [Fact]
    public void Build_PrioritizesOnScreenMauiActionAfterManyOffscreenNodes()
    {
        var children = new JsonArray();
        for (var index = 0; index < 80; index++)
        {
            children.Add(new JsonObject
            {
                ["id"] = $"offscreen-{index}",
                ["typeId"] = 1,
                ["automationId"] = $"offscreen-control-{index}",
                ["label"] = $"Offscreen {index}",
                ["flags"] = 3,
                ["bounds"] = CompactBounds(20, 900 + (index * 30), 300, 24),
                ["children"] = new JsonArray()
            });
        }

        children.Add(new JsonObject
        {
            ["id"] = "open-guide",
            ["typeId"] = 2,
            ["automationId"] = "area-open3d-guide-button",
            ["label"] = "3D Guide",
            ["flags"] = 3,
            ["bounds"] = CompactBounds(104, 697, 181, 44),
            ["children"] = new JsonArray()
        });
        var visualTree = new JsonObject
        {
            ["format"] = "ansight.maui.visual-tree.compact.v2",
            ["rootScope"] = "currentPage",
            ["types"] = new JsonArray("AreaPage", "Label", "Border"),
            ["flagBits"] = new JsonObject
            {
                ["visible"] = 1,
                ["enabled"] = 2,
                ["currentPage"] = 4,
                ["activePage"] = 8
            },
            ["coordinateSpace"] = Bounds(0, 0, 390, 844),
            ["currentPage"] = new JsonObject
            {
                ["id"] = "area-page",
                ["typeId"] = 0,
                ["label"] = "Secret Garden"
            },
            ["root"] = new JsonObject
            {
                ["id"] = "area-page",
                ["typeId"] = 0,
                ["label"] = "Secret Garden",
                ["flags"] = 15,
                ["bounds"] = CompactBounds(0, 0, 390, 844),
                ["children"] = children
            }
        };

        var observation = VisualTreeObservation.Build(
            visualTree,
            "session-001",
            "com.example.app",
            "maui.get_visual_tree");

        var nodes = Descendants(Assert.IsType<JsonObject>(Assert.IsType<JsonObject>(observation)["root"]));
        Assert.Contains(nodes, node =>
            node["automationId"]?.GetValue<string>() == "area-open3d-guide-button");
        Assert.True(observation["truncated"]?.GetValue<bool>());
    }

    [Fact]
    public void Build_PreservesFrameworkNavigationContainersForGraphStructure()
    {
        var visualTree = new JsonObject
        {
            ["format"] = "ansight.maui.visual-tree.compact.v2",
            ["types"] = new JsonArray("Microsoft.Maui.Controls.Shell", "Microsoft.Maui.Controls.TabBar", "Microsoft.Maui.Controls.ContentPage"),
            ["flagBits"] = new JsonObject
            {
                ["visible"] = 1,
                ["enabled"] = 2
            },
            ["root"] = new JsonObject
            {
                ["id"] = "shell",
                ["typeId"] = 0,
                ["flags"] = 3,
                ["children"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["id"] = "tabs",
                        ["typeId"] = 1,
                        ["flags"] = 3,
                        ["children"] = new JsonArray
                        {
                            new JsonObject
                            {
                                ["id"] = "home",
                                ["typeId"] = 2,
                                ["label"] = "Home",
                                ["flags"] = 3,
                                ["children"] = new JsonArray()
                            }
                        }
                    }
                }
            }
        };

        var observation = Assert.IsType<JsonObject>(VisualTreeObservation.Build(
            visualTree,
            "session-001",
            "com.example.app",
            "maui.get_visual_tree"));

        Assert.Equal("maui", observation["framework"]?.GetValue<string>());
        var structureNodes = Assert.IsType<JsonArray>(observation["structureNodes"]);
        Assert.Contains(structureNodes.OfType<JsonObject>(), node =>
            node["type"]?.GetValue<string>().Contains("Shell", StringComparison.Ordinal) == true);
        Assert.Contains(structureNodes.OfType<JsonObject>(), node =>
            node["type"]?.GetValue<string>().Contains("TabBar", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void Build_AppliesUIKitAndSwiftUiControllersToAMixedIosTree()
    {
        var observation = Assert.IsType<JsonObject>(VisualTreeObservation.Build(
            NativeTree(
                "ios",
                "apple.uikit",
                "UIKit.UITabBarController",
                "SwiftUI.UIHostingController<RootView>",
                "SwiftUI.TabView"),
            "session-001",
            "com.example.app",
            VisualTreeContract.NativeToolId));

        var frameworks = Assert.IsType<JsonArray>(observation["frameworks"])
            .Select(value => Assert.IsAssignableFrom<JsonValue>(value).GetValue<string>())
            .ToArray();
        Assert.Equal(["ios-uikit", "ios-swiftui"], frameworks);
        Assert.Null(observation["graphStructureGuidance"]);
        var controllers = Assert.IsType<JsonArray>(observation["graphStructureControllers"]);
        Assert.Equal(frameworks, controllers.Select(controller => controller!["framework"]!.GetValue<string>()));
        Assert.All(controllers, controller => Assert.Null(controller!["guidance"]));
    }

    [Fact]
    public void Build_AppliesAndroidViewsAndComposeControllersToAMixedAndroidTree()
    {
        var observation = Assert.IsType<JsonObject>(VisualTreeObservation.Build(
            NativeTree(
                "android",
                "android.views",
                "android.widget.FrameLayout",
                "androidx.compose.ui.platform.AndroidComposeView",
                "androidx.navigation.compose.NavHost"),
            "session-001",
            "com.example.app",
            VisualTreeContract.NativeToolId));

        var frameworks = Assert.IsType<JsonArray>(observation["frameworks"])
            .Select(value => Assert.IsAssignableFrom<JsonValue>(value).GetValue<string>())
            .ToArray();
        Assert.Equal(["android-views", "android-compose"], frameworks);
        Assert.Null(observation["graphStructureGuidance"]);
        var controllers = Assert.IsType<JsonArray>(observation["graphStructureControllers"]);
        Assert.Equal(frameworks, controllers.Select(controller => controller!["framework"]!.GetValue<string>()));
        Assert.All(controllers, controller => Assert.Null(controller!["guidance"]));
    }

    [Fact]
    public void Build_OmitsStaticGuidanceWhilePreservingTreeEvidence()
    {
        var content = new JsonObject
        {
            ["sessionId"] = "session-001",
            ["appId"] = "com.example.app",
            ["toolId"] = VisualTreeContract.NativeToolId,
            ["persistedVisualTree"] = new JsonObject { ["artifactPath"] = "/evidence/tree.json" },
            ["evidence"] = new JsonObject { ["captureId"] = "capture-001" },
            ["payload"] = new JsonObject
            {
                ["result"] = NativeTree("ios", "apple.uikit", "UIKit.UITabBarController")
            }
        };
        var original = content.ToJsonString();

        var observation = Assert.IsType<JsonObject>(VisualTreeObservation.Build(content));

        Assert.Equal(original, content.ToJsonString());
        Assert.Equal("ios-uikit", observation["framework"]?.GetValue<string>());
        Assert.Equal("session-001", observation["sessionId"]?.GetValue<string>());
        Assert.Equal(VisualTreeContract.NativeToolId, observation["toolId"]?.GetValue<string>());
        Assert.Null(observation["graphStructureGuidance"]);
        Assert.All(Assert.IsType<JsonArray>(observation["graphStructureControllers"]),
            controller => Assert.Null(controller!["guidance"]));
        Assert.True(JsonNode.DeepEquals(content["persistedVisualTree"], observation["persistedVisualTree"]));
        Assert.True(JsonNode.DeepEquals(content["evidence"], observation["evidence"]));
        Assert.NotEmpty(Assert.IsType<JsonArray>(observation["structureNodes"]));
    }

    [Fact]
    public void ResolveVisualTree_DistinguishesAppKitAndUnknownNativeWithoutMauiFallback()
    {
        var appKit = NavigationControllerCatalog.ResolveVisualTree(
            new JsonObject
            {
                ["platform"] = "macos",
                ["source"] = "native",
                ["adapter"] = "apple.appkit",
                ["types"] = new JsonArray("AppKit.NSWindow", "AppKit.NSTabViewController")
            },
            VisualTreeContract.NativeToolId);
        var unknown = NavigationControllerCatalog.ResolveVisualTree(
            new JsonObject
            {
                ["platform"] = "dotnet",
                ["source"] = "native",
                ["types"] = new JsonArray("CustomSurface")
            },
            VisualTreeContract.NativeToolId);

        Assert.Equal("macos-appkit", Assert.Single(appKit).Framework);
        var unknownController = Assert.Single(unknown);
        Assert.Equal("native-unknown", unknownController.Framework);
        var guidance = NavigationGuidance.Read(unknownController.Framework);
        Assert.DoesNotContain("MAUI", guidance, StringComparison.Ordinal);
        Assert.DoesNotContain("Treat .NET", guidance, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_OmitsUIKitPageCoveredByLaterVisiblePage()
    {
        var observation = Assert.IsType<JsonObject>(VisualTreeObservation.Build(
            CoveredUIKitTree(),
            "session-001",
            "com.example.app",
            VisualTreeContract.NativeToolId));

        var automationIds = Descendants(Assert.IsType<JsonObject>(observation["root"]))
            .Select(node => node["automationId"]?.GetValue<string>())
            .OfType<string>()
            .ToArray();
        Assert.DoesNotContain("HomeMenuPage", automationIds);
        Assert.DoesNotContain("HomeAccountButton", automationIds);
        Assert.Contains("MapPage", automationIds);
    }

    [Theory]
    [InlineData("ios.swiftui.get_navigation_state", "ios-swiftui")]
    [InlineData("android.compose.get_navigation_state", "android-compose")]
    [InlineData("android.views.get_navigation_state", "android-views")]
    [InlineData("macos.appkit.get_navigation_state", "macos-appkit")]
    public void ResolveNavigationTool_SelectsExactToolkitController(string toolId, string expectedFramework)
    {
        var controller = NavigationControllerCatalog.ResolveNavigationTool(toolId);

        Assert.Equal(expectedFramework, controller?.Framework);
    }

    private static IEnumerable<JsonObject> Descendants(JsonObject node)
    {
        if (node["children"] is not JsonArray children)
        {
            yield break;
        }

        foreach (var child in children.OfType<JsonObject>())
        {
            yield return child;
            foreach (var descendant in Descendants(child))
            {
                yield return descendant;
            }
        }
    }

    private static JsonObject Bounds(double x, double y, double width, double height)
        => new()
        {
            ["x"] = x,
            ["y"] = y,
            ["width"] = width,
            ["height"] = height
        };

    private static JsonArray CompactBounds(double x, double y, double width, double height)
        => new(x, y, width, height, x, y, width, height);

    private static JsonObject NativeTree(
        string platform,
        string adapter,
        params string[] typeNames)
    {
        var children = new JsonArray();
        for (var index = 1; index < typeNames.Length; index++)
        {
            children.Add(new JsonObject
            {
                ["id"] = $"node-{index}",
                ["typeId"] = index,
                ["flags"] = 3,
                ["children"] = new JsonArray()
            });
        }
        return new JsonObject
        {
            ["format"] = VisualTreeContract.NativeFormat,
            ["platform"] = platform,
            ["source"] = "native",
            ["adapter"] = adapter,
            ["types"] = new JsonArray(typeNames.Select(static typeName => (JsonNode?)typeName).ToArray()),
            ["flagBits"] = new JsonObject
            {
                ["visible"] = 1,
                ["enabled"] = 2
            },
            ["root"] = new JsonObject
            {
                ["id"] = "root",
                ["typeId"] = 0,
                ["flags"] = 3,
                ["children"] = children
            }
        };
    }

    private static JsonObject CoveredUIKitTree()
        => new()
        {
            ["format"] = VisualTreeContract.NativeFormat,
            ["platform"] = "ios",
            ["source"] = "native",
            ["adapter"] = "apple.uikit",
            ["types"] = new JsonArray("UIKit.UIView", "Microsoft.Maui.Platform.ContentView", "UIKit.UIButton"),
            ["flagBits"] = new JsonObject
            {
                ["visible"] = 1,
                ["enabled"] = 2
            },
            ["coordinateSpace"] = Bounds(0, 0, 390, 844),
            ["root"] = new JsonObject
            {
                ["id"] = "root",
                ["typeId"] = 0,
                ["flags"] = 3,
                ["bounds"] = Bounds(0, 0, 390, 844),
                ["children"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["id"] = "flyout",
                        ["typeId"] = 0,
                        ["flags"] = 3,
                        ["bounds"] = Bounds(0, 0, 312, 844),
                        ["children"] = new JsonArray
                        {
                            new JsonObject
                            {
                                ["id"] = "home",
                                ["typeId"] = 1,
                                ["automationId"] = "HomeMenuPage",
                                ["flags"] = 3,
                                ["bounds"] = Bounds(0, 0, 312, 844),
                                ["children"] = new JsonArray
                                {
                                    new JsonObject
                                    {
                                        ["id"] = "account",
                                        ["typeId"] = 2,
                                        ["automationId"] = "HomeAccountButton",
                                        ["flags"] = 3,
                                        ["bounds"] = Bounds(20, 100, 200, 44),
                                        ["supportedActions"] = new JsonArray("tap"),
                                        ["children"] = new JsonArray()
                                    }
                                }
                            }
                        }
                    },
                    new JsonObject
                    {
                        ["id"] = "detail",
                        ["typeId"] = 0,
                        ["flags"] = 3,
                        ["bounds"] = Bounds(0, 0, 390, 844),
                        ["children"] = new JsonArray
                        {
                            new JsonObject
                            {
                                ["id"] = "map",
                                ["typeId"] = 1,
                                ["automationId"] = "MapPage",
                                ["flags"] = 3,
                                ["bounds"] = Bounds(0, 0, 390, 844),
                                ["children"] = new JsonArray()
                            }
                        }
                    }
                }
            }
        };
}
