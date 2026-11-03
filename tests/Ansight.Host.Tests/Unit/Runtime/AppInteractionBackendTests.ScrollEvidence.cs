namespace Ansight.Host.Tests.Unit.Runtime;

public sealed partial class AppInteractionBackendTests
{
    [Theory]
    [InlineData("bounce", "unchanged", true)]
    [InlineData("movement", "changed", true)]
    [InlineData("unstable", "unknown", false)]
    public async Task ScrollEvidenceWaitsForBounceAndInertiaToSettle(string scenario, string expected, bool isSettled)
    {
        var session = CreateModalTestSession();
        var router = new UiInputRouter();
        var accessible = CreateModalNativeTree();
        accessible["root"]!["children"] = new JsonArray();
        router.ConfigureAccessibility(new ModalSelectorAccessibility(accessible));
        using var scope = router.BeginTargetScope(session.SessionId, "simulator-001");
        var selector = LiveUiSelector.Parse(new JsonObject { ["automationId"] = "area-share-options-list" });
        var before = await Capture(0, default);
        var initial = await Capture(20, default);
        var samples = 0;

        var settled = await LiveUiScrollSettlement.CaptureAsync(initial, selector, token =>
        {
            samples++;
            var offset = scenario switch
            {
                "bounce" => samples == 1 ? 5 : 0,
                "movement" => 20,
                _ => 20 + samples * 3
            };
            return Capture(offset, token);
        }, default);

        var target = Assert.Single(LiveUiNodeQuery.Find(before.Capture!.Root, selector, before.Capture.TypeRegistry));
        var observation = LiveUiScrollObservation.Compare(before.Capture, settled.Result.Capture,
            target, selector, settled.IsSettled);
        Assert.Equal(isSettled, settled.IsSettled);
        Assert.Equal(expected, observation["status"]!.GetValue<string>());
        Assert.InRange(samples, 2, 8);
        if (scenario == "bounce")
        {
            Assert.Equal(4, samples);
        }

        async Task<LiveUiCaptureResult> Capture(int offset, CancellationToken token)
        {
            var tree = CreateModalNativeTree();
            foreach (var node in GetModalCopyNodes(tree))
            {
                var bounds = node["bounds"]!.AsArray();
                bounds[1] = bounds[1]!.GetValue<double>() - offset;
            }

            var bridge = new ModalSelectorBridge(session.SessionId, tree);
            return await LiveUiTreeCapture.CaptureForSelectorAsync(session, bridge, router,
                "ansight_scroll_ui", null, selector, token, allowCached: false, targetSource: "ui.get_visual_tree");
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TruncatedNativeScrollTargetExpandsOnlyTheFreshMatchingSubtree(bool mismatchedBounds)
    {
        var session = CreateModalTestSession();
        var native = CreateModalNativeTree();
        var list = native["root"]!["children"]![0]!["children"]![0]!.AsObject();
        list["childCount"] = 2;
        var subtree = native.DeepClone().AsObject();
        subtree["root"] = list.DeepClone();
        subtree["coordinateSpace"] = new JsonObject { ["x"] = 0, ["y"] = 450, ["width"] = 400, ["height"] = 300 };
        if (mismatchedBounds)
        {
            subtree["root"]!["bounds"] = new JsonArray(0, 50, 400, 300);
        }

        list["children"]!.AsArray().RemoveAt(1);
        var bridge = new ModalSelectorBridge(session.SessionId, native) { Subtree = subtree };
        var selector = LiveUiSelector.Parse(new JsonObject { ["automationId"] = "area-share-options-list" });
        var router = new UiInputRouter();
        var accessible = native.DeepClone().AsObject();
        accessible["root"]!["children"] = new JsonArray();
        router.ConfigureAccessibility(new ModalSelectorAccessibility(accessible));
        using var targetScope = router.BeginTargetScope(session.SessionId, "simulator-001");

        var result = await LiveUiTreeCapture.CaptureForSelectorAsync(session, bridge, router,
            "ansight_scroll_ui", null, selector, default, targetSource: "ui.get_visual_tree");

        Assert.Equal("options", bridge.LastSubtreeRoot);
        var capture = result.Capture!;
        var target = Assert.Single(LiveUiNodeQuery.Find(capture.Root, selector, capture.TypeRegistry));
        Assert.Equal(!mismatchedBounds, LiveUiScrollObservation.IsCompleteArea(target.Node, capture.Payload, capture.TypeRegistry));
        Assert.Equal(new LiveUiBounds(0, 0, 400, 800), capture.Viewport);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CollapsedAccessibilityScrollUsesNativeEvidenceOnlyForTheSameVisibleArea(bool mismatchedBounds)
    {
        var session = CreateModalTestSession();
        var native = CreateModalNativeTree();
        var list = native["root"]!["children"]![0]!["children"]![0]!.AsObject();
        if (mismatchedBounds)
        {
            list["bounds"] = new JsonArray(0, 50, 400, 300);
        }

        var accessible = DeviceAccessibilityTreeNormalizer.NormalizeIos(
            """
            <AppiumAUT>
              <XCUIElementTypeApplication type="XCUIElementTypeApplication" name="Test App" enabled="true" visible="true" x="0" y="0" width="400" height="800">
                <XCUIElementTypeOther type="XCUIElementTypeOther" name="area-share-options-list" label="Options" enabled="true" visible="true" x="0" y="450" width="400" height="300" />
              </XCUIElementTypeApplication>
            </AppiumAUT>
            """, viewportWidth: 400, viewportHeight: 800, maxNodes: 40, maxDepth: 16);
        var bridge = new ModalSelectorBridge(session.SessionId, native);
        var router = new UiInputRouter();
        router.ConfigureAccessibility(new ModalSelectorAccessibility(accessible));
        using var targetScope = router.BeginTargetScope(session.SessionId, "simulator-001");
        var selector = LiveUiSelector.Parse(new JsonObject { ["automationId"] = "area-share-options-list" });
        var initial = await LiveUiTreeCapture.CaptureForSelectorAsync(session, bridge, router,
            "ansight_scroll_ui", null, selector, default);
        var capture = initial.Capture!;
        var target = Assert.Single(LiveUiNodeQuery.Find(capture.Root, selector, capture.TypeRegistry));

        var evidence = await LiveUiScrollEvidence.PrepareAsync(capture, target, selector,
            bridge, router, "ansight_scroll_ui", null, default);

        Assert.Equal(mismatchedBounds ? LiveUiTreeCapture.DeviceAccessibilityToolId : "ui.get_visual_tree", evidence.Capture.ToolId);
        Assert.Equal(LiveUiTreeCapture.DeviceAccessibilityToolId, capture.ToolId);
        Assert.Equal(mismatchedBounds ? "unknown" : "unchanged",
            LiveUiScrollObservation.Compare(evidence.Capture, evidence.Capture, evidence.Target, evidence.Selector)["status"]!.GetValue<string>());
    }
}
