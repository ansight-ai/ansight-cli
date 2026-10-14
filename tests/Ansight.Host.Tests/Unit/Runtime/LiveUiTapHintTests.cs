using System.Text.Json.Nodes;

namespace Ansight.Host.Tests.Unit.Runtime;

public sealed class LiveUiTapHintTests
{
    [Theory]
    [InlineData("unchanged", true)]
    [InlineData("different-provider", false)]
    [InlineData("changed-label", false)]
    [InlineData("changed-bounds", false)]
    [InlineData("conflicting-selector", false)]
    [InlineData("ambiguous", false)]
    [InlineData("hidden", false)]
    public void TransientIdRecoveryRequiresUniqueUnchangedEvidenceFromTheSameProvider(string scenario, bool recovered)
    {
        var root = SearchTree();
        var node = root["children"]![1]!.AsObject();
        node["id"] = "before";
        var registry = CreateRegistry();
        var viewport = new LiveUiBounds(0, 0, 400, 850);
        var selector = LiveUiSelector.Parse(new JsonObject { ["nodeId"] = "before", ["role"] = "text", ["visible"] = true });
        var match = Assert.Single(LiveUiNodeQuery.Find(root, selector, registry));
        var arguments = new JsonObject
        {
            ["targetSource"] = scenario == "different-provider" ? "ui.get_visual_tree" : "device.accessibility",
            ["targetFingerprint"] = LiveUiTapTarget.Fingerprint(root, match, viewport)
        };
        node["id"] = "after";
        if (scenario == "changed-label") node["text"] = "Different result";
        if (scenario == "changed-bounds") node["bounds"]!["y"] = 450;
        if (scenario == "hidden") node["visible"] = false;
        if (scenario == "conflicting-selector") selector = LiveUiSelector.Parse(new JsonObject { ["nodeId"] = "before", ["text"] = "Not the result" });
        if (scenario == "ambiguous")
        {
            var duplicate = node.DeepClone();
            duplicate["id"] = "duplicate";
            root["children"]!.AsArray().Add(duplicate);
        }
        var refreshed = LiveUiTapTarget.RefreshTransientNodeId(root, registry, viewport, selector,
            "device.accessibility", arguments);
        Assert.Equal(recovered ? "after" : "before", refreshed.NodeId);
        if (recovered) Assert.Same(node, Assert.Single(LiveUiNodeQuery.Find(root, refreshed, registry)).Node);
    }

    [Fact]
    public void ProviderBoundHintUsesStableIdentityAndRejectsChangedTargetEvidence()
    {
        var root = SearchTree();
        root["children"]![1]!["automationId"] = "result";
        root["children"]![1]!["id"] = "transient-first";
        var registry = CreateRegistry();
        var viewport = new LiveUiBounds(0, 0, 400, 850);
        var query = LiveUiSelector.Parse(new JsonObject { ["automationId"] = "result" });
        var match = Assert.Single(LiveUiNodeQuery.Find(root, query, registry));
        var hint = FindLiveUiTool.BuildTapHint(root, match, viewport, query,
            targetSource: LiveUiTreeCapture.DeviceAccessibilityToolId);
        var selector = hint!["selector"]!;
        Assert.Equal(LiveUiTreeCapture.DeviceAccessibilityToolId, selector["targetSource"]!.GetValue<string>());
        Assert.Equal("result", selector["automationId"]!.GetValue<string>());
        Assert.Null(selector["nodeId"]);
        var fingerprint = selector["targetFingerprint"]!.GetValue<string>();
        const string legacyFingerprint = "F920307378C3B5ED3EC661DFEAC911058044D458175DE3667A86E681CA539B79";
        Assert.Equal(20, fingerprint.Length);
        Assert.Equal(legacyFingerprint[..20], fingerprint);
        Assert.True(LiveUiTapTarget.MatchesFingerprint(root, match, viewport, legacyFingerprint));
        Assert.True(LiveUiTapTarget.MatchesFingerprint(root, match, viewport, fingerprint));
        Assert.False(LiveUiTapTarget.MatchesFingerprint(root, match, viewport, fingerprint[..^1]));
        Assert.False(LiveUiTapTarget.MatchesFingerprint(root, match, viewport, "X" + fingerprint[1..]));
        match.Node["id"] = "transient-second";
        Assert.Equal(fingerprint, LiveUiTapTarget.Fingerprint(root, match, viewport));
        match.Node["text"] = "Different area";
        Assert.NotEqual(fingerprint, LiveUiTapTarget.Fingerprint(root, match, viewport));
    }

    [Fact]
    public void BuildTapHint_ReplaysResultLabelWithoutTheTextFieldIndex()
    {
        var root = SearchTree();
        var registry = CreateRegistry();
        var query = LiveUiSelector.Parse(new JsonObject { ["text"] = "Kalymnos", ["visible"] = true });
        var matches = LiveUiNodeQuery.Find(root, query, registry);
        Assert.Equal(2, matches.Count);

        var hint = FindLiveUiTool.BuildTapHint(root, matches[1], new LiveUiBounds(0, 0, 400, 850), query);
        var selector = Assert.IsType<JsonObject>(hint?["selector"]);
        Assert.Equal("text", selector["role"]!.GetValue<string>());
        Assert.Equal("StaticText", selector["type"]!.GetValue<string>());
        Assert.False(selector.ContainsKey("index"));
        Assert.Same(matches[1].Node, Assert.Single(LiveUiNodeQuery.Find(root, LiveUiSelector.Parse(selector), registry)).Node);

        var compact = UiResultProjection.Project(new JsonObject
        {
            ["matches"] = new JsonArray(new JsonObject { ["text"] = "Kalymnos", ["tapHint"] = hint }),
            ["totalMatches"] = 1
        }, UiProjectionOptions.Model);
        Assert.True(JsonNode.DeepEquals(selector, compact["matches"]![0]!["tapHint"]!["selector"]));
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("disabled")]
    [InlineData("offscreen")]
    [InlineData("hidden")]
    public void BuildTapHint_DoesNotInventAmbiguousOrUnavailableTargets(string state)
    {
        var root = SearchTree();
        var label = root["children"]![1]!;
        if (state == "duplicate")
        {
            root["children"]!.AsArray().Add(label.DeepClone());
        }
        else if (state == "offscreen")
        {
            label["bounds"]!["y"] = 900;
        }
        else
        {
            label[state == "disabled" ? "enabled" : "visible"] = false;
        }
        var registry = CreateRegistry();
        var query = LiveUiSelector.Parse(new JsonObject { ["text"] = "Kalymnos", ["role"] = "text" });
        var match = LiveUiNodeQuery.Find(root, query, registry)[0];

        Assert.Null(FindLiveUiTool.BuildTapHint(root, match, new LiveUiBounds(0, 0, 400, 850), query));
    }

    private static JsonObject SearchTree()
        => new()
        {
            ["typeId"] = 0,
            ["visible"] = true,
            ["enabled"] = true,
            ["children"] = new JsonArray(Node("TextField", "textbox", 50), Node("StaticText", "text", 300))
        };

    [Fact]
    public void BuildTapHint_UsesGuardedStableIndexBeforeTransientNodeId()
    {
        var root = SearchTree();
        var first = root["children"]![1]!.AsObject();
        first["automationId"] = "result-label";
        first["id"] = "first-result";
        var second = first.DeepClone().AsObject();
        second["id"] = "second-result";
        second["bounds"]!["y"] = 450;
        root["children"]!.AsArray().Add(second);
        var registry = CreateRegistry();
        var query = LiveUiSelector.Parse(new JsonObject { ["text"] = "Kalymnos" });
        var match = LiveUiNodeQuery.Find(root, query, registry)[2];

        var hint = FindLiveUiTool.BuildTapHint(root, match, new LiveUiBounds(0, 0, 400, 850), query);

        Assert.Null(hint?["selector"]?["nodeId"]);
        Assert.Equal(1, hint?["selector"]?["index"]?.GetValue<int>());
        Assert.NotNull(hint?["selector"]?["targetFingerprint"]);
    }

    [Fact]
    public void BuildTapHint_ScopesRepeatedNativeIdsToTheirObservedAncestor()
    {
        var root = SearchTree();
        var tab = Node("StaticText", "group", 750);
        tab["automationId"] = "1";
        tab["text"] = "Map";
        tab["id"] = "old-native-id";
        var other = tab.DeepClone().AsObject();
        other["id"] = "other-native-id";
        root["children"]!.AsArray().Add(other);
        root["children"]!.AsArray().Add(new JsonObject
        {
            ["automationId"] = "bottom-tabs", ["visible"] = true, ["enabled"] = true,
            ["children"] = new JsonArray(tab)
        });
        var registry = CreateRegistry();
        var viewport = new LiveUiBounds(0, 0, 400, 850);
        var match = LiveUiNodeQuery.Find(root, LiveUiSelector.Parse(new JsonObject { ["nodeId"] = "old-native-id" }), registry)[0];
        var hint = FindLiveUiTool.BuildTapHint(root, match, viewport, LiveUiSelector.Parse(null),
            targetSource: RemoteAppToolIds.UiGetVisualTree);
        var selector = hint!["selector"]!;
        Assert.Equal("1", selector["automationId"]!.GetValue<string>());
        Assert.Equal("bottom-tabs", selector["ancestorAutomationId"]!.GetValue<string>());
        Assert.Null(selector["nodeId"]);
        tab["id"] = "new-native-id";
        var resolved = Assert.Single(LiveUiNodeQuery.Find(root, LiveUiSelector.Parse(selector.AsObject()), registry));
        Assert.Same(tab, resolved.Node);
        Assert.Equal(selector["targetFingerprint"]!.GetValue<string>(), LiveUiTapTarget.Fingerprint(root, resolved, viewport));
    }

    [Fact]
    public void BuildTapHint_RecomputesEachDuplicateIndexAndRetainsContextThroughModelProjection()
    {
        var root = SearchTree();
        var second = root["children"]![1]!.DeepClone().AsObject();
        second["bounds"]!["y"] = 500;
        root["children"]!.AsArray().Add(second);
        var region = Node("StaticText", "text", 545);
        region["text"] = "Greece · Crag";
        root["children"]!.AsArray().Add(region);
        var registry = CreateRegistry();
        var viewport = new LiveUiBounds(0, 0, 400, 850);
        var query = LiveUiSelector.Parse(new JsonObject { ["text"] = "Kalymnos" });
        var matches = LiveUiNodeQuery.Find(root, query, registry);
        Assert.Equal(3, matches.Count); // Input echo plus the two real results.

        for (var index = 1; index < matches.Count; index++)
        {
            var match = matches[index];
            var hint = FindLiveUiTool.BuildTapHint(root, match, viewport, query);
            var selector = Assert.IsType<JsonObject>(hint?["selector"]);
            Assert.Equal(index - 1, selector["index"]!.GetValue<int>());
            var exactMatches = LiveUiNodeQuery.PrioritizeActionTargets(
                LiveUiNodeQuery.Find(root, LiveUiSelector.Parse(selector), registry), root, viewport);
            var selected = exactMatches[selector["index"]!.GetValue<int>()];
            Assert.Same(match.Node, selected.Node);
            Assert.Equal(LiveUiTapTarget.Fingerprint(root, selected, viewport), selector["targetFingerprint"]!.GetValue<string>());

            var compact = UiResultProjection.Project(new JsonObject
            {
                ["matches"] = new JsonArray(new JsonObject
                {
                    ["text"] = "Kalymnos", ["tapHint"] = hint,
                    ["nearbyText"] = LiveUiTapTarget.NearbyText(root, match, viewport)
                })
            }, UiProjectionOptions.Model);
            Assert.True(JsonNode.DeepEquals(selector, compact["matches"]![0]!["tapHint"]!["selector"]));
            if (index == 2) Assert.Contains("Greece", compact["matches"]![0]!["nearbyText"]!.ToJsonString());
        }
    }

    [Fact]
    public void NearbyText_ExcludesInputValuesHiddenAndDistantLabels()
    {
        var root = SearchTree();
        foreach (var kind in new[] { "near", "hidden", "distant", "password" })
        {
            var label = Node("StaticText", "text", kind == "distant" ? 650 : 345);
            label["text"] = kind;
            if (kind == "hidden") label["visible"] = false;
            if (kind == "password") label["isPassword"] = true;
            root["children"]!.AsArray().Add(label);
        }
        var input = Node("TextField", "textbox", 345);
        input["text"] = "private input";
        root["children"]!.AsArray().Add(input);
        var match = LiveUiNodeQuery.Find(root,
            LiveUiSelector.Parse(new JsonObject { ["text"] = "Kalymnos", ["role"] = "text" }), CreateRegistry())[0];

        var context = LiveUiTapTarget.NearbyText(root, match, new LiveUiBounds(0, 0, 400, 850));

        Assert.Equal("near", Assert.Single(context)!["text"]!.GetValue<string>());
    }

    [Fact]
    public void CompactObservation_DoesNotOverrideResolverRefusalWithAnAncestorGuess()
    {
        var result = UiResultProjection.Project(new JsonObject
        {
            ["matches"] = new JsonArray(new JsonObject
            {
                ["text"] = "Kalymnos",
                ["tapHint"] = null,
                ["ancestorPath"] = new JsonArray(new JsonObject { ["automationId"] = "results-panel" })
            }),
            ["totalMatches"] = 1
        }, UiProjectionOptions.Model);

        Assert.Null(result["matches"]![0]!["tapHint"]);
    }

    private static JsonObject Node(string type, string role, int y)
        => new()
        {
            ["typeId"] = type == "TextField" ? 1 : 2,
            ["role"] = role,
            ["text"] = "Kalymnos",
            ["visible"] = true,
            ["enabled"] = true,
            ["supportedActions"] = role == "textbox" ? new JsonArray("tap", "typeText") : new JsonArray(),
            ["bounds"] = new JsonObject { ["x"] = 20, ["y"] = y, ["width"] = 300, ["height"] = 40 }
        };

    private static VisualTreeTypeRegistry CreateRegistry()
        => VisualTreeTypeRegistry.FromPayload(new JsonObject
        {
            ["types"] = new JsonArray("Application", "TextField", "StaticText")
        });
}
