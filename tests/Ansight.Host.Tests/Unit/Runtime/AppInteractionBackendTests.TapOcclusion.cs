namespace Ansight.Host.Tests.Unit.Runtime;

public sealed partial class AppInteractionBackendTests
{
    [Theory]
    [InlineData(false, 0, "ios", true)]
    [InlineData(true, 0, "ios", false)]
    [InlineData(false, 5, "ios", false)]
    [InlineData(false, 0, "android", false)]
    public async Task NativeStackingDistinguishesHeaderBehindCloseFromForegroundObstruction(
        bool headerAfterButton, int headerZ, string platform, bool allowed)
    {
        var session = CreateModalTestSession();
        var tree = CreateModalNativeTree();
        tree["platform"] = platform;

        var header = new JsonObject
        {
            ["id"] = "header",
            ["typeId"] = 1,
            ["role"] = "text",
            ["label"] = "Filters",
            ["visible"] = true,
            ["enabled"] = true,
            ["supportedActions"] = new JsonArray("tap"),
            ["bounds"] = new JsonArray(0, 490, 400, 50),
            ["z"] = headerZ
        };
        var close = ModalNativeCopy("close", 500);
        close["automationId"] = "close";
        tree["root"]!["children"] = headerAfterButton
            ? new JsonArray(close, header) : new JsonArray(header, close);

        var bridge = new ModalSelectorBridge(session.SessionId, tree);
        var result = await LiveUiTreeCapture.CaptureNativeVisualTreeAsync(session,
            bridge, "ansight_tap_ui", null, default);
        Assert.True(bridge.IncludesComputedStyles);

        var capture = result.Capture!;
        var target = Assert.Single(LiveUiNodeQuery.Find(capture.Root,
            LiveUiSelector.Parse(new JsonObject { ["automationId"] = "close" }), capture.TypeRegistry));

        var point = LiveUiUnobstructedPoint.Resolve(capture, target, new LiveUiScreenPoint(336, 516), false);

        Assert.Equal(allowed, point.HasValue);
    }

    [Fact]
    public async Task NativeStackingUsesTheSharedParentRatherThanDescendantLocalZ()
    {
        var session = CreateModalTestSession();
        var tree = CreateModalNativeTree();
        var behind = ModalNativeCopy("behind", 500);
        behind["z"] = 100;

        var targetNode = ModalNativeCopy("target", 500);
        targetNode["automationId"] = "target";
        tree["root"]!["children"] = new JsonArray(
            new JsonObject
            {
                ["id"] = "background-branch",
                ["typeId"] = 1,
                ["visible"] = true,
                ["enabled"] = true,
                ["bounds"] = new JsonArray(0, 0, 400, 800),
                ["children"] = new JsonArray(behind)
            },
            new JsonObject
            {
                ["id"] = "foreground-branch",
                ["typeId"] = 1,
                ["visible"] = true,
                ["enabled"] = true,
                ["bounds"] = new JsonArray(0, 0, 400, 800),
                ["children"] = new JsonArray(targetNode)
            });

        var result = await LiveUiTreeCapture.CaptureNativeVisualTreeAsync(session,
            new ModalSelectorBridge(session.SessionId, tree), "ansight_tap_ui", null, default);
        var capture = result.Capture!;
        var target = Assert.Single(LiveUiNodeQuery.Find(capture.Root,
            LiveUiSelector.Parse(new JsonObject { ["automationId"] = "target" }), capture.TypeRegistry));

        Assert.NotNull(LiveUiUnobstructedPoint.Resolve(capture, target, new LiveUiScreenPoint(336, 516), false));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DuplicatePhysicalNodeProjectionDoesNotOccludeItselfButAnotherControlDoes(bool sameRuntimeId)
    {
        var session = CreateModalTestSession();
        var tree = CreateModalNativeTree();
        var first = ModalNativeCopy("physical-button", 500);
        first["automationId"] = "target";

        var duplicate = first.DeepClone().AsObject();
        duplicate["id"] = sameRuntimeId ? "physical-button" : "another-button";
        tree["root"]!["children"] = new JsonArray(first, duplicate);

        var result = await LiveUiTreeCapture.CaptureNativeVisualTreeAsync(session,
            new ModalSelectorBridge(session.SessionId, tree), "ansight_tap_ui", null, default);
        var capture = result.Capture!;
        var selector = LiveUiSelector.Parse(new JsonObject { ["automationId"] = "target" });
        var target = LiveUiNodeQuery.Find(capture.Root, selector, capture.TypeRegistry)[0];

        var point = LiveUiUnobstructedPoint.Resolve(capture, target, new LiveUiScreenPoint(336, 516), false);

        Assert.Equal(sameRuntimeId, point.HasValue);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task TapAvoidsOverlappingSiblingAndRefusesFullyCoveredOrExplicitOccludedPoints(bool fullyCovered, bool explicitPoint)
    {
        var session = CreateModalTestSession();
        var tree = CreateModalNativeTree();
        tree["root"]!["children"] = new JsonArray(
            new JsonObject
            {
                ["id"] = "about",
                ["typeId"] = 2,
                ["automationId"] = "map-about",
                ["visible"] = true,
                ["enabled"] = true,
                ["role"] = "button",
                ["bounds"] = new JsonArray(340, 704, 44, 42)
            },
            new JsonObject
            {
                ["id"] = "profile",
                ["typeId"] = 2,
                ["automationId"] = "profile-tab",
                ["visible"] = true,
                ["enabled"] = true,
                ["role"] = "button",
                ["bounds"] = new JsonArray(320, fullyCovered ? 700 : 721, 80, 100)
            });

        var selector = LiveUiSelector.Parse(new JsonObject { ["automationId"] = "map-about" });
        var result = await LiveUiTreeCapture.CaptureNativeVisualTreeAsync(session,
            new ModalSelectorBridge(session.SessionId, tree), "ansight_tap_ui", null, default);
        var capture = result.Capture!;
        var target = Assert.Single(LiveUiNodeQuery.Find(capture.Root, selector, capture.TypeRegistry));

        var point = LiveUiUnobstructedPoint.Resolve(capture, target, new LiveUiScreenPoint(362, 725), explicitPoint);

        if (fullyCovered || explicitPoint)
        {
            Assert.Null(point);
        }
        else
        {
            Assert.NotNull(point);
            Assert.InRange(point.Value.Y, 704, 720.99);
            Assert.InRange(point.Value.X, 340, 384);
        }
    }
}
