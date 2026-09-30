using System.Text.Json.Nodes;
using Ansight.Host.Runtime.Operations;

namespace Ansight.Host.Tests.Unit.Devices;

public sealed class DeviceAccessibilityTreeNormalizerTests
{
    [Fact]
    public void NormalizeIosSimulator_CompactsResidentAxpTranslatorSnapshot()
    {
        var source = JsonNode.Parse("""
            {
              "format": "ansight.core-simulator-accessibility.raw.v1",
              "viewportWidth": 400,
              "viewportHeight": 800,
              "rawNodeCount": 12,
              "elements": [
                {
                  "role": "AXApplication",
                  "label": "Example",
                  "x": 0,
                  "y": 0,
                  "width": 400,
                  "height": 800,
                  "enabled": true,
                  "depth": 0
                },
                {
                  "role": "AXButton",
                  "id": "continue-button",
                  "label": "Continue",
                  "x": 100,
                  "y": 600,
                  "width": 200,
                  "height": 50,
                  "enabled": true,
                  "selected": true,
                  "depth": 2
                },
                {
                  "role": "AXSlider",
                  "id": "volume-slider",
                  "value": "0.75",
                  "x": 40,
                  "y": 500,
                  "width": 320,
                  "height": 40,
                  "enabled": true,
                  "depth": 2
                }
              ]
            }
            """)!;

        var payload = DeviceAccessibilityTreeNormalizer.NormalizeIosSimulator(
            source,
            maxNodes: 40,
            maxDepth: 16);

        Assert.Equal("core-simulator-ax-service", payload["source"]?.GetValue<string>());
        Assert.Equal("resident-axptranslator", payload["provider"]?.GetValue<string>());
        Assert.Equal(12, payload["rawNodeCount"]?.GetValue<int>());
        Assert.Equal(3, payload["nativeNodeCount"]?.GetValue<int>());
        Assert.True(VisualTreeTypeRegistry.TryCreateCompactV2(
            payload,
            out var typeRegistry,
            out var root));

        var buttonSelector = LiveUiSelector.Parse(new JsonObject
        {
            ["automationId"] = "continue-button",
            ["role"] = "button"
        });
        var button = Assert.Single(LiveUiNodeQuery.Find(root!, buttonSelector, typeRegistry!));
        Assert.Equal("Continue", LiveUiNodeQuery.ReadText(button.Node));
        Assert.Contains("tap", LiveUiNodeQuery.ReadSupportedActions(button.Node, typeRegistry!));
        Assert.True(button.Node["selected"]?.GetValue<bool>());

        var sliderSelector = LiveUiSelector.Parse(new JsonObject
        {
            ["automationId"] = "volume-slider",
            ["role"] = "slider"
        });
        var slider = Assert.Single(LiveUiNodeQuery.Find(root!, sliderSelector, typeRegistry!));
        Assert.Equal("0.75", slider.Node["value"]?.GetValue<string>());
        Assert.False(payload["keyboardVisible"]?.GetValue<bool>());
    }

    [Fact]
    public void NormalizeIosSimulator_ReportsVisibleSystemKeyboard()
    {
        var source = JsonNode.Parse("""
            {
              "format": "ansight.core-simulator-accessibility.raw.v1",
              "viewportWidth": 400,
              "viewportHeight": 800,
              "rawNodeCount": 3,
              "elements": [
                {
                  "role": "AXApplication",
                  "label": "Example",
                  "x": 0,
                  "y": 0,
                  "width": 400,
                  "height": 800,
                  "enabled": true,
                  "depth": 0
                },
                {
                  "role": "AXKeyboard",
                  "x": 0,
                  "y": 500,
                  "width": 400,
                  "height": 300,
                  "enabled": true,
                  "depth": 1
                },
                {
                  "role": "AXKey",
                  "label": "A",
                  "x": 10,
                  "y": 520,
                  "width": 30,
                  "height": 40,
                  "enabled": true,
                  "depth": 2
                }
              ]
            }
            """)!;

        var payload = DeviceAccessibilityTreeNormalizer.NormalizeIosSimulator(
            source,
            maxNodes: 40,
            maxDepth: 16);

        Assert.True(payload["keyboardVisible"]?.GetValue<bool>());
    }

    [Fact]
    public void NormalizeIosSimulator_CompactsAxeAxServiceJsonAndHandlesNumericValues()
    {
        var source = JsonNode.Parse("""
            [
              {
                "AXFrame": "{{0, 0}, {400, 800}}",
                "frame": { "x": 0, "y": 0, "width": 400, "height": 800 },
                "AXLabel": "Example",
                "type": "Application",
                "enabled": true,
                "role": "AXApplication",
                "children": [
                  {
                    "frame": { "x": 0, "y": 0, "width": 400, "height": 800 },
                    "type": "Group",
                    "enabled": true,
                    "role": "AXGroup",
                    "children": [
                      {
                        "AXUniqueId": "continue-button",
                        "AXLabel": "Continue",
                        "frame": { "x": 100, "y": 600, "width": 200, "height": 50 },
                        "type": "Button",
                        "enabled": true,
                        "role": "AXButton",
                        "children": []
                      },
                      {
                        "AXIdentifier": "volume-slider",
                        "AXValue": 0.75,
                        "frame": { "x": 40, "y": 500, "width": 320, "height": 40 },
                        "type": "Slider",
                        "enabled": true,
                        "role": "AXSlider",
                        "children": []
                      }
                    ]
                  }
                ]
              }
            ]
            """)!;

        var payload = DeviceAccessibilityTreeNormalizer.NormalizeIosSimulator(
            source,
            maxNodes: 40,
            maxDepth: 16);

        Assert.Equal("core-simulator-ax-service", payload["source"]?.GetValue<string>());
        Assert.True(VisualTreeTypeRegistry.TryCreateCompactV2(
            payload,
            out var typeRegistry,
            out var root));
        var buttonSelector = LiveUiSelector.Parse(new JsonObject
        {
            ["automationId"] = "continue-button",
            ["role"] = "button"
        });
        var button = Assert.Single(LiveUiNodeQuery.Find(root!, buttonSelector, typeRegistry!));
        Assert.Equal("Continue", LiveUiNodeQuery.ReadText(button.Node));
        Assert.Contains("tap", LiveUiNodeQuery.ReadSupportedActions(button.Node, typeRegistry!));

        var sliderSelector = LiveUiSelector.Parse(new JsonObject
        {
            ["automationId"] = "volume-slider",
            ["role"] = "slider"
        });
        var slider = Assert.Single(LiveUiNodeQuery.Find(root!, sliderSelector, typeRegistry!));
        Assert.Equal("0.75", slider.Node["value"]?.GetValue<string>());
        Assert.Equal(4, payload["nodeCount"]?.GetValue<int>());
        Assert.DoesNotContain(
            LiveUiNodeQuery.Enumerate(root!, typeRegistry!),
            candidate => string.Equals(
                candidate.TypeRegistry.Resolve(candidate.Node),
                "Group",
                StringComparison.Ordinal));
    }

    [Fact]
    public void NormalizeIos_PrunesLayoutWrappersAndReturnsNormalizedActionTargets()
    {
        const string source = """
            <?xml version="1.0" encoding="UTF-8"?>
            <AppiumAUT>
              <XCUIElementTypeApplication type="XCUIElementTypeApplication" name="Red Point" enabled="true" visible="true" x="0" y="0" width="400" height="800">
                <XCUIElementTypeOther type="XCUIElementTypeOther" enabled="true" visible="true" x="0" y="0" width="400" height="800">
                  <XCUIElementTypeTabBar type="XCUIElementTypeTabBar" label="Tab Bar" enabled="true" visible="true" x="0" y="720" width="400" height="80">
                    <XCUIElementTypeButton type="XCUIElementTypeButton" name="crags-tab" label="Crags" enabled="true" visible="true" x="100" y="720" width="100" height="50" />
                  </XCUIElementTypeTabBar>
                </XCUIElementTypeOther>
              </XCUIElementTypeApplication>
            </AppiumAUT>
            """;

        var payload = DeviceAccessibilityTreeNormalizer.NormalizeIos(
            source,
            viewportWidth: 400,
            viewportHeight: 800,
            maxNodes: 40,
            maxDepth: 16);

        Assert.True(VisualTreeTypeRegistry.TryCreateCompactV2(
            payload,
            out var typeRegistry,
            out var root));
        var selector = LiveUiSelector.Parse(new JsonObject
        {
            ["text"] = "Crags",
            ["role"] = "button",
            ["visible"] = true
        });
        var match = Assert.Single(LiveUiNodeQuery.Find(root!, selector, typeRegistry!));
        var bounds = Assert.IsType<LiveUiBounds>(LiveUiNodeQuery.ReadBounds(match.Node));
        Assert.Equal(0.25, bounds.X, precision: 6);
        Assert.Equal(0.9, bounds.Y, precision: 6);
        Assert.Equal(0.25, bounds.Width, precision: 6);
        Assert.Contains("tap", LiveUiNodeQuery.ReadSupportedActions(match.Node, typeRegistry!));
        Assert.DoesNotContain(
            LiveUiNodeQuery.Enumerate(root!, typeRegistry!),
            candidate => string.Equals(
                candidate.TypeRegistry.Resolve(candidate.Node),
                "XCUIElementTypeOther",
                StringComparison.Ordinal));
    }

    [Fact]
    public void NormalizeAndroid_PreservesAccessibilityIdentifiersAndActions()
    {
        const string source = """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <hierarchy rotation="0">
              <node index="0" text="" resource-id="" class="android.widget.FrameLayout" package="com.example" content-desc="" checkable="false" checked="false" clickable="false" enabled="true" focusable="false" focused="false" scrollable="false" long-clickable="false" password="false" selected="false" bounds="[0,0][1080,2400]">
                <node index="0" text="Search" resource-id="com.example:id/search" class="android.widget.EditText" package="com.example" content-desc="Search routes" checkable="false" checked="false" clickable="true" enabled="true" focusable="true" focused="false" scrollable="false" long-clickable="false" password="false" selected="false" bounds="[108,240][972,360]" />
              </node>
            </hierarchy>
            """;

        var payload = DeviceAccessibilityTreeNormalizer.NormalizeAndroid(
            source,
            viewportWidth: 1080,
            viewportHeight: 2400,
            maxNodes: 40,
            maxDepth: 16);

        Assert.True(VisualTreeTypeRegistry.TryCreateCompactV2(
            payload,
            out var typeRegistry,
            out var root));
        var selector = LiveUiSelector.Parse(new JsonObject
        {
            ["automationId"] = "com.example:id/search",
            ["role"] = "textbox"
        });
        var match = Assert.Single(LiveUiNodeQuery.Find(root!, selector, typeRegistry!));
        Assert.Equal("Search", LiveUiNodeQuery.ReadText(match.Node));
        Assert.Equal(
            ["tap", "typeText", "focus"],
            LiveUiNodeQuery.ReadSupportedActions(match.Node, typeRegistry!));
        Assert.Equal(2, payload["nodeCount"]?.GetValue<int>());
        Assert.False(payload["keyboardVisible"]?.GetValue<bool>());
    }

    [Fact]
    public void NormalizeAndroid_ReportsVisibleInputMethodKeyboard()
    {
        const string source = """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <hierarchy rotation="0">
              <node index="0" text="" resource-id="" class="android.widget.FrameLayout" package="com.example" enabled="true" bounds="[0,0][1080,2400]">
                <node index="0" text="q" resource-id="com.google.android.inputmethod.latin:id/key_pos_0_0" class="android.view.View" package="com.google.android.inputmethod.latin" content-desc="q" clickable="true" enabled="true" bounds="[0,1600][108,1760]" />
              </node>
            </hierarchy>
            """;

        var payload = DeviceAccessibilityTreeNormalizer.NormalizeAndroid(
            source,
            viewportWidth: 1080,
            viewportHeight: 2400,
            maxNodes: 40,
            maxDepth: 16);

        Assert.True(payload["keyboardVisible"]?.GetValue<bool>());
    }

    [Fact]
    public void NormalizeAndroid_ExcludesAnsightOverlayAndPreservesAppOwnedOverlayControls()
    {
        const string source = """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <hierarchy rotation="0">
              <node index="0" text="" resource-id="" class="android.widget.FrameLayout" package="com.example" content-desc="" clickable="false" enabled="true" bounds="[0,0][1080,2400]">
                <node index="0" text="App overlay action" resource-id="com.example:id/overlay-action" class="com.example.OverlaySurface" package="com.example" content-desc="" clickable="true" enabled="true" bounds="[40,120][480,240]" />
                <node index="1" text="" resource-id="ansight.overlay.surface" class="ai.ansight.runtime.AndroidUiEvidence$OverlaySurface" package="com.example" content-desc="ansight.overlay.surface" clickable="true" enabled="true" bounds="[0,0][1080,2400]">
                  <node index="0" text="Injected child" resource-id="com.example:id/injected-child" class="android.widget.Button" package="com.example" content-desc="" clickable="true" enabled="true" bounds="[0,0][1080,2400]" />
                </node>
              </node>
            </hierarchy>
            """;

        var payload = DeviceAccessibilityTreeNormalizer.NormalizeAndroid(
            source,
            viewportWidth: 1080,
            viewportHeight: 2400,
            maxNodes: 40,
            maxDepth: 16);

        Assert.True(VisualTreeTypeRegistry.TryCreateCompactV2(
            payload,
            out var typeRegistry,
            out var root));
        Assert.Single(LiveUiNodeQuery.Find(
            root!,
            LiveUiSelector.Parse(new JsonObject
            {
                ["automationId"] = "com.example:id/overlay-action"
            }),
            typeRegistry!));
        Assert.DoesNotContain(
            LiveUiNodeQuery.Enumerate(root!, typeRegistry!),
            candidate => candidate.TypeRegistry.Resolve(candidate.Node) is
                "ai.ansight.runtime.AndroidUiEvidence$OverlaySurface");
        Assert.Empty(LiveUiNodeQuery.Find(
            root!,
            LiveUiSelector.Parse(new JsonObject
            {
                ["automationId"] = "com.example:id/injected-child"
            }),
            typeRegistry!));
        Assert.Equal(2, payload["nodeCount"]?.GetValue<int>());
    }
}
