using System.Text.Json.Nodes;
using Ansight.Host.Runtime.Operations;
using Ansight.Host.Runtime.Operations.Tools.UiAutomation;

namespace Ansight.Host.Tests.Unit.Runtime;

public sealed class VisualTreePresentationNormalizerTests
{
    [Fact]
    public void Create_HidesUIKitBranchCoveredByLaterPageWithoutChangingRawCapture()
    {
        var raw = UIKitTree();

        var presentation = VisualTreePresentationNormalizer.Create(raw);

        Assert.Null(Find(raw, "HomeMenuPage")["visible"]);
        Assert.True(IsVisible(raw, "HomeMenuPage"));
        Assert.False(IsVisible(presentation, "HomeMenuPage"));
        Assert.False(IsVisible(presentation, "HomeAccountButton"));
        Assert.True(IsVisible(presentation, "MapPage"));
        Assert.True(IsVisible(presentation, "AnsightOverlay"));
    }

    [Fact]
    public void Create_DoesNotTreatTransparentUIKitOverlayAsOpaque()
    {
        var raw = UIKitTree(includeFlyout: false);

        var presentation = VisualTreePresentationNormalizer.Create(raw);

        Assert.True(IsVisible(presentation, "MapPage"));
        Assert.True(IsVisible(presentation, "AnsightOverlay"));
    }

    [Fact]
    public void Create_UsesOpaqueBackgroundWhenFrontBranchHasNoPageIdentity()
    {
        var raw = UIKitTree(includeFlyout: false, includeOverlay: false);
        var root = Assert.IsType<JsonObject>(raw["root"]);
        Assert.IsType<JsonArray>(root["children"]).Add(new JsonObject
        {
            ["id"] = "solid-cover",
            ["typeId"] = 0,
            ["flags"] = 3,
            ["bounds"] = Bounds(0, 0, 390, 844),
            ["visual"] = new JsonObject
            {
                ["background"] = "#FFFFFFFF",
                ["opacity"] = 1
            },
            ["children"] = new JsonArray()
        });

        var presentation = VisualTreePresentationNormalizer.Create(raw);

        Assert.False(IsVisible(presentation, "MapPage"));
    }

    [Fact]
    public void Create_KeepsEarlierBranchVisibleWhenFrontPageDoesNotFullyCoverIt()
    {
        var raw = UIKitTree(includeOverlay: false);
        var mapPage = Find(raw, "MapPage");
        mapPage["bounds"] = Bounds(312, 0, 78, 844);
        var mapBranch = Assert.IsType<JsonObject>(mapPage.Parent?.Parent);
        mapBranch["bounds"] = Bounds(312, 0, 78, 844);

        var presentation = VisualTreePresentationNormalizer.Create(raw);

        Assert.True(IsVisible(presentation, "HomeMenuPage"));
        Assert.True(IsVisible(presentation, "MapPage"));
    }

    [Fact]
    public void BuildSessionVisualTreeSnapshotPayload_PresentsNormalizedCloneAndRetainsRawSnapshot()
    {
        var raw = UIKitTree();
        var snapshot = new SessionVisualTreeSnapshot
        {
            SnapshotId = "tree-1",
            CapturedAtUtc = DateTimeOffset.Parse("2026-09-02T05:35:17Z"),
            VisualTreeKind = VisualTreeContract.NativeKind,
            VisualTreeFormat = VisualTreeContract.NativeFormat,
            RuntimePlatform = "ios",
            Source = "native",
            NodeCount = 8,
            Payload = raw
        };

        var result = PayloadJson.BuildSessionVisualTreeSnapshotPayload(snapshot);
        var presentation = Assert.IsType<JsonObject>(result["payload"]);

        Assert.False(IsVisible(presentation, "HomeMenuPage"));
        Assert.True(IsVisible(raw, "HomeMenuPage"));
        Assert.Null(Find(raw, "HomeMenuPage")["visible"]);
    }

    [Fact]
    public void NormalizeSerializedSession_PresentsSnapshotsWithoutChangingSourceDocument()
    {
        var raw = UIKitTree();
        var serializedSession = new JsonObject
        {
            ["visualTreeSnapshots"] = new JsonArray
            {
                new JsonObject
                {
                    ["snapshotId"] = "tree-1",
                    ["payload"] = raw.DeepClone()
                }
            }
        };

        VisualTreePresentationNormalizer.NormalizeSerializedSession(serializedSession);

        var snapshots = Assert.IsType<JsonArray>(serializedSession["visualTreeSnapshots"]);
        var snapshot = Assert.IsType<JsonObject>(snapshots[0]);
        var presentation = Assert.IsType<JsonObject>(snapshot["payload"]);
        Assert.False(IsVisible(presentation, "HomeMenuPage"));
        Assert.True(IsVisible(raw, "HomeMenuPage"));
    }

    private static JsonObject UIKitTree(
        bool includeFlyout = true,
        bool includeOverlay = true)
    {
        var children = new JsonArray();
        if (includeFlyout)
        {
            children.Add(new JsonObject
            {
                ["id"] = "flyout-branch",
                ["typeId"] = 0,
                ["flags"] = 3,
                ["bounds"] = Bounds(0, 0, 312, 844),
                ["children"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["id"] = "home-menu-page",
                        ["typeId"] = 1,
                        ["automationId"] = "HomeMenuPage",
                        ["flags"] = 3,
                        ["bounds"] = Bounds(0, 0, 312, 844),
                        ["children"] = new JsonArray
                        {
                            new JsonObject
                            {
                                ["id"] = "account-button",
                                ["typeId"] = 3,
                                ["automationId"] = "HomeAccountButton",
                                ["flags"] = 3,
                                ["bounds"] = Bounds(20, 100, 200, 44),
                                ["supportedActions"] = new JsonArray("tap"),
                                ["children"] = new JsonArray()
                            }
                        }
                    }
                }
            });
        }

        children.Add(new JsonObject
        {
            ["id"] = "map-branch",
            ["typeId"] = 0,
            ["flags"] = 3,
            ["bounds"] = Bounds(0, 0, 390, 844),
            ["children"] = new JsonArray
            {
                new JsonObject
                {
                    ["id"] = "map-page",
                    ["typeId"] = 1,
                    ["automationId"] = "MapPage",
                    ["flags"] = 3,
                    ["bounds"] = Bounds(0, 0, 390, 844),
                    ["children"] = new JsonArray()
                }
            }
        });

        if (includeOverlay)
        {
            children.Add(new JsonObject
            {
                ["id"] = "ansight-overlay",
                ["typeId"] = 2,
                ["automationId"] = "AnsightOverlay",
                ["flags"] = 3,
                ["bounds"] = Bounds(0, 0, 390, 844),
                ["visual"] = new JsonObject
                {
                    ["background"] = "#00FFFFFF",
                    ["opacity"] = 1
                },
                ["children"] = new JsonArray()
            });
        }

        return new JsonObject
        {
            ["format"] = VisualTreeContract.NativeFormat,
            ["source"] = "native",
            ["platform"] = "ios",
            ["adapter"] = "apple.uikit",
            ["types"] = new JsonArray("UIKit.UIView", "Microsoft.Maui.Platform.ContentView", "Ansight.PassthroughView", "UIKit.UIButton"),
            ["flagBits"] = new JsonObject
            {
                ["visible"] = 1,
                ["enabled"] = 2
            },
            ["coordinateSpace"] = Bounds(0, 0, 390, 844),
            ["root"] = new JsonObject
            {
                ["id"] = "window",
                ["typeId"] = 0,
                ["flags"] = 3,
                ["bounds"] = Bounds(0, 0, 390, 844),
                ["children"] = children
            }
        };
    }

    private static JsonObject Find(JsonObject payload, string automationId)
    {
        var typeRegistry = VisualTreeTypeRegistry.FromPayload(payload);
        var root = Assert.IsType<JsonObject>(payload["root"]);
        return LiveUiNodeQuery.Enumerate(root, typeRegistry)
            .Single(match => string.Equals(
                LiveUiNodeQuery.ReadAutomationId(match.Node),
                automationId,
                StringComparison.Ordinal))
            .Node;
    }

    private static bool IsVisible(JsonObject payload, string automationId)
    {
        var typeRegistry = VisualTreeTypeRegistry.FromPayload(payload);
        var root = Assert.IsType<JsonObject>(payload["root"]);
        var match = LiveUiNodeQuery.Enumerate(root, typeRegistry)
            .Single(candidate => string.Equals(
                LiveUiNodeQuery.ReadAutomationId(candidate.Node),
                automationId,
                StringComparison.Ordinal));
        return LiveUiNodeQuery.IsEffectivelyVisible(match);
    }

    private static JsonObject Bounds(double x, double y, double width, double height)
        => new()
        {
            ["x"] = x,
            ["y"] = y,
            ["width"] = width,
            ["height"] = height
        };
}
