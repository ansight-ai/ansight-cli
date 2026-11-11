using Ansight.Host.Tests.TestSupport;
using Ansight.Tools;

namespace Ansight.Host.Tests.Unit.Runtime;

public sealed partial class AppInteractionBackendTests
{
    [Fact]
    public void StructuralContainerDoesNotAdvertiseTapButItsButtonDoes()
    {
        var payload = CreateModalNativeTree();
        var sheet = payload["root"]!["children"]![0]!.AsObject();
        sheet["supportedActions"] = new JsonArray();
        Assert.True(VisualTreeTypeRegistry.TryCreateCompactV2(payload, out var types, out var root));

        var viewport = LiveUiNodeQuery.ReadCoordinateSpace(payload);
        var sheetSelector = LiveUiSelector.Parse(new JsonObject { ["automationId"] = "area-share-sheet" });
        var sheetMatch = Assert.Single(LiveUiNodeQuery.Find(root!, sheetSelector, types!));
        var buttonSelector = LiveUiSelector.Parse(ModalCopySelector());
        var button = LiveUiNodeQuery.Find(root!, buttonSelector, types!)[0];

        Assert.Null(FindLiveUiTool.BuildTapHint(root!, sheetMatch, viewport, sheetSelector, payload));
        Assert.NotNull(FindLiveUiTool.BuildTapHint(root!, button, viewport, buttonSelector, payload));
    }

    [Theory]
    [InlineData("ansight_find_ui", false, true)]
    [InlineData("ansight_wait_for_ui", false, true)]
    [InlineData("ansight_find_ui", true, false)]
    [InlineData("ansight_tap_ui", false, false)]
    public async Task FlattenedSheetContainerCanBeInspectedThroughCorroboratedChildren(
        string operation, bool hideSecondChild, bool found)
    {
        var session = CreateModalTestSession();
        var native = CreateModalNativeTree();

        if (hideSecondChild)
        {
            GetModalCopyNodes(native)[1]["visible"] = false;
        }

        var router = new UiInputRouter();
        router.ConfigureAccessibility(new ModalSelectorAccessibility(CreateFlatModalAccessibility()));

        using var scope = router.BeginTargetScope(session.SessionId, "simulator-001");
        var selector = LiveUiSelector.Parse(new JsonObject { ["automationId"] = "area-share-sheet" });
        var result = await LiveUiTreeCapture.CaptureNativeForSelectorAsync(session,
            new ModalSelectorBridge(session.SessionId, native), router, operation, null, selector, default);

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(found, LiveUiNodeQuery.Find(result.Capture!.Root, selector, result.Capture.TypeRegistry).Count > 0);
    }

    [Theory]
    [InlineData("routes-tab", true)]
    [InlineData("different-overlay", false)]
    public async Task RenderedLabelIsCorroboratedByItsAccessibleAncestorIdentity(string foregroundId, bool allowed)
    {
        var session = CreateModalTestSession();
        var native = CreateModalNativeTree();
        var control = GetModalCopyNodes(native)[0];
        control["automationId"] = "routes-tab";
        control["label"] = "Routes, 1 matches";
        control["children"] = new JsonArray(new JsonObject
        {
            ["id"] = "caption",
            ["typeId"] = 1,
            ["label"] = "Routes",
            ["role"] = "text",
            ["visible"] = true,
            ["enabled"] = false,
            ["bounds"] = new JsonArray(320, 500, 32, 32)
        });

        var accessible = DeviceAccessibilityTreeNormalizer.NormalizeIos(
            $"""
            <AppiumAUT>
              <XCUIElementTypeApplication type="XCUIElementTypeApplication" name="Test App" enabled="true" visible="true" x="0" y="0" width="400" height="800">
                <XCUIElementTypeButton type="XCUIElementTypeButton" name="{foregroundId}" label="Routes, 1 matches" enabled="true" visible="true" x="320" y="500" width="32" height="32" />
              </XCUIElementTypeApplication>
            </AppiumAUT>
            """, 400, 800, 40, 16);
        var router = new UiInputRouter();
        router.ConfigureAccessibility(new ModalSelectorAccessibility(accessible));

        using var scope = router.BeginTargetScope(session.SessionId, "simulator-001");
        var selector = LiveUiSelector.Parse(new JsonObject { ["text"] = "Routes", ["matchMode"] = "exact" });

        var result = await LiveUiTreeCapture.CaptureForSelectorAsync(session,
            new ModalSelectorBridge(session.SessionId, native), router, "ansight_tap_ui", null,
            selector, default, targetSource: RemoteAppToolIds.UiGetVisualTree);

        Assert.Equal(allowed, result.IsSuccess);
    }

    [Theory]
    [InlineData(0, 0.645)]
    [InlineData(1, 0.7825)]
    public async Task ModalSelector_WaitThenTapResolvesFlattenedAncestorAndDuplicateIndex(int index, double expectedY)
    {
        using var environment = new TestEnvironment();
        var state = new RuntimeState(new SessionCaptureStore(environment.ApplicationPaths));
        var sessionId = state.CreateSession("test.app", "Test App", IPAddress.Loopback, null, null);
        state.SetSessionDeviceProfile(sessionId, null,
            """{"device":{"nativeDeviceId":"simulator-001","osName":"iOS"}}""");

        var bridge = new ModalSelectorBridge(sessionId, CreateModalNativeTree());
        var composition = new MefHostComposition(environment.ApplicationPaths,
            new FileBackedEncryptedStorage(environment.SecureStorageFilePath));
        using var dispatcher = new OperationDispatcher(state, environment.ApplicationPaths,
            composition.Get<IKnownAppStore>(), composition.Get<IPairingConfigService>(), composition.Get<IPairingConfigCache>(),
            composition.Get<AppService>(), composition.Get<PairingService>(), composition.Get<DotNetProfilingService>(),
            composition.Get<NativeProfilingService>(), bridge);
        var input = new InputDriver();
        dispatcher.ConfigureUiInputDriver(input);
        dispatcher.ConfigureUiAccessibilityDriver(new ModalSelectorAccessibility(CreateFlatModalAccessibility()));

        var arguments = ModalCopySelector(index);
        arguments["sessionId"] = sessionId;
        arguments["timeoutMs"] = 500;

        var wait = await dispatcher.CallToolAsync("ansight_wait_for_ui", arguments);
        Assert.False(wait.Payload?["isError"]?.GetValue<bool>(), wait.Payload?.ToJsonString());
        arguments.Remove("timeoutMs");
        arguments["includeScreenshot"] = false;

        var tap = await dispatcher.CallToolAsync("ansight_tap_ui", arguments);

        Assert.False(tap.Payload?["isError"]?.GetValue<bool>(), tap.Payload?.ToJsonString());
        Assert.Equal(1, input.Taps);
        Assert.Equal(0.84, input.LastTap!.NormalizedX, 6);
        Assert.Equal(expectedY, input.LastTap.NormalizedY, 6);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ModalSelector_UsesCorroboratedNativeHierarchyForBothInteractionPaths(bool useInteractionPath)
    {
        var session = CreateModalTestSession();
        var bridge = new ModalSelectorBridge(session.SessionId, CreateModalNativeTree());
        var router = new UiInputRouter();
        router.ConfigureAccessibility(new ModalSelectorAccessibility(CreateFlatModalAccessibility()));

        using var target = router.BeginTargetScope(session.SessionId, "simulator-001");
        var selector = LiveUiSelector.Parse(ModalCopySelector());

        var result = useInteractionPath
            ? await LiveUiTreeCapture.CaptureForInteractionAsync(session, bridge, router,
                "test-interaction", selector, RemoteAppToolIds.MauiGetVisualTree, CancellationToken.None)
            : await LiveUiTreeCapture.CaptureForSelectorAsync(session, bridge, router,
                "ansight_tap_ui", null, selector, CancellationToken.None);

        Assert.Equal(RemoteAppToolIds.UiGetVisualTree, result.Capture?.ToolId);
        Assert.Equal([LiveUiTreeCapture.DeviceAccessibilityToolId, RemoteAppToolIds.UiGetVisualTree], result.AttemptedToolIds);
        Assert.Equal(2, LiveUiNodeQuery.Find(result.Capture!.Root, selector, result.Capture.TypeRegistry).Count);
        Assert.Equal(1, bridge.Captures);
    }

    [Theory]
    [InlineData("wrong-ancestor")]
    [InlineData("wrong-index")]
    [InlineData("wrong-position")]
    [InlineData("wrong-id")]
    [InlineData("missing-bounds")]
    [InlineData("hidden-accessibility")]
    [InlineData("disabled-accessibility")]
    [InlineData("duplicate-native")]
    [InlineData("duplicate-accessibility")]
    [InlineData("failed-native-capture")]
    public async Task ModalSelector_RejectsUncorroboratedTargetsAndPreservesSelector(string scenario)
    {
        var session = CreateModalTestSession();
        var accessibility = CreateFlatModalAccessibility();
        var native = CreateModalNativeTree();
        var arguments = ModalCopySelector();
        var nativeCopy = GetModalCopyNodes(native)[0];
        var accessibleCopy = GetModalCopyNodes(accessibility)[0];

        switch (scenario)
        {
            case "wrong-ancestor":
                arguments["ancestorAutomationId"] = "other-sheet";
                break;
            case "wrong-index":
                arguments["index"] = 2;
                break;
            case "wrong-position":
                nativeCopy["bounds"] = new JsonArray(320, 100, 32, 32);
                break;
            case "wrong-id":
                accessibleCopy["automationId"] = "different-copy-button";
                break;
            case "missing-bounds":
                nativeCopy.Remove("bounds");
                break;
            case "hidden-accessibility":
                accessibleCopy["visible"] = false;
                break;
            case "disabled-accessibility":
                accessibleCopy["enabled"] = false;
                break;
            case "duplicate-native":
            case "duplicate-accessibility":
                var original = scenario == "duplicate-native" ? nativeCopy : accessibleCopy;
                var duplicate = original.DeepClone().AsObject();
                duplicate["id"] = "duplicate-copy";
                original.Parent!.AsArray().Add(duplicate);
                break;
        }

        var bridge = new ModalSelectorBridge(session.SessionId, native) { FailCapture = scenario == "failed-native-capture" };
        var router = new UiInputRouter();
        router.ConfigureAccessibility(new ModalSelectorAccessibility(accessibility));

        using var target = router.BeginTargetScope(session.SessionId, "simulator-001");
        var selector = LiveUiSelector.Parse(arguments);

        var result = await LiveUiTreeCapture.CaptureForSelectorAsync(session, bridge, router,
            "ansight_tap_ui", null, selector, CancellationToken.None);

        Assert.Equal(LiveUiTreeCapture.DeviceAccessibilityToolId, result.Capture?.ToolId);
        Assert.Empty(LiveUiNodeQuery.Find(result.Capture!.Root, selector, result.Capture.TypeRegistry));
    }

    [Theory]
    [InlineData("ui.get_visual_tree", 1)]
    [InlineData("avalonia.get_visual_tree", 1)]
    [InlineData("avalonia.get_logical_tree", 1)]
    [InlineData("avalonia.get_accessibility_tree", 1)]
    [InlineData("device.accessibility", 0)]
    public async Task TargetSourceKeepsTheObservedProviderEvenWhenAnotherProviderAlsoMatches(string source, int nativeCaptures)
    {
        var session = CreateModalTestSession();
        var accessible = CreateFlatModalAccessibility();
        accessible["root"]!["children"]!.AsArray().RemoveAt(0);

        var bridge = new ModalSelectorBridge(session.SessionId, CreateModalNativeTree())
        {
            ToolId = source == LiveUiTreeCapture.DeviceAccessibilityToolId
                ? RemoteAppToolIds.UiGetVisualTree
                : source
        };
        var router = new UiInputRouter();
        router.ConfigureAccessibility(new ModalSelectorAccessibility(accessible));

        using var target = router.BeginTargetScope(session.SessionId, "simulator-001");
        var selector = LiveUiSelector.Parse(new JsonObject { ["automationId"] = "area-share-option-copy-button" });
        var result = await LiveUiTreeCapture.CaptureForSelectorAsync(session, bridge, router,
            "ansight_tap_ui", null, selector, CancellationToken.None, targetSource: source);
        Assert.Equal(source, result.Capture?.ToolId);
        Assert.Equal(nativeCaptures, bridge.Captures);
    }

    [Theory]
    [InlineData("ui.get_visual_tree")]
    [InlineData("avalonia.get_visual_tree")]
    [InlineData("avalonia.get_logical_tree")]
    [InlineData("avalonia.get_accessibility_tree")]
    public async Task TargetSourceCannotBypassAnActiveModal(string source)
    {
        var session = CreateModalTestSession();
        var bridge = new ModalSelectorBridge(session.SessionId, CreateModalNativeTree());
        var router = new UiInputRouter();
        router.ConfigureAccessibility(new ModalSelectorAccessibility(CreateFlatModalAccessibility()));

        using var target = router.BeginTargetScope(session.SessionId, "simulator-001");
        var selector = LiveUiSelector.Parse(new JsonObject { ["automationId"] = "background-button" });
        var result = await LiveUiTreeCapture.CaptureForSelectorAsync(session, bridge, router,
            "ansight_tap_ui", null, selector, CancellationToken.None, targetSource: source);
        Assert.True(result.IsSuccess);
        Assert.Equal(LiveUiTreeCapture.DeviceAccessibilityToolId, result.Capture!.ToolId);
        Assert.Empty(LiveUiNodeQuery.Find(result.Capture.Root, selector, result.Capture.TypeRegistry));
        Assert.Equal(0, bridge.Captures);
    }

    private static JsonObject[] GetModalCopyNodes(JsonObject payload)
    {
        Assert.True(VisualTreeTypeRegistry.TryCreateCompactV2(payload, out var types, out var root));

        return LiveUiNodeQuery.Find(root!, LiveUiSelector.Parse(new JsonObject
        {
            ["automationId"] = "area-share-option-copy-button"
        }), types!).Select(match => match.Node).ToArray();
    }

    [Theory]
    [InlineData(false, null, "conflict", false)]
    [InlineData(false, "ui.get_visual_tree", "conflict", false)]
    [InlineData(true, null, "conflict", false)]
    [InlineData(false, "ui.get_visual_tree", "same-label", true)]
    [InlineData(true, null, "same-label", true)]
    [InlineData(false, "ui.get_visual_tree", "elsewhere", true)]
    [InlineData(false, "ui.get_visual_tree", "hidden", true)]
    public async Task FlattenedForegroundControlsCannotReceiveInputIntendedForSdkTargets(
        bool interaction, string? targetSource, string scenario, bool allowed)
    {
        var session = CreateModalTestSession();
        var native = CreateModalNativeTree();
        var sdkTarget = GetModalCopyNodes(native)[0];
        sdkTarget["automationId"] = "settings-select-language-button";
        sdkTarget["label"] = "Change Language";

        var accessibility = DeviceAccessibilityTreeNormalizer.NormalizeIos(
            """
            <AppiumAUT>
              <XCUIElementTypeApplication type="XCUIElementTypeApplication" name="Test App" enabled="true" visible="true" x="0" y="0" width="400" height="800">
                <XCUIElementTypeButton type="XCUIElementTypeButton" label="Ελληνικά" enabled="true" visible="true" x="310" y="490" width="50" height="50" />
              </XCUIElementTypeApplication>
            </AppiumAUT>
            """, 400, 800, 40, 16);
        Assert.True(VisualTreeTypeRegistry.TryCreateCompactV2(accessibility, out var registry, out var root));

        var foreground = Assert.Single(LiveUiNodeQuery.Find(root!, LiveUiSelector.Parse(new JsonObject
        {
            ["text"] = "Ελληνικά"
        }), registry!)).Node;

        if (scenario == "same-label")
        {
            sdkTarget.Remove("label");
            sdkTarget["children"] = new JsonArray(new JsonObject
            {
                ["typeId"] = 1,
                ["label"] = "Ελληνικά",
                ["role"] = "text",
                ["visible"] = true,
                ["enabled"] = true,
                ["bounds"] = new JsonArray(320, 500, 32, 32)
            });
        }

        if (scenario == "elsewhere")
        {
            foreground["bounds"] = new JsonArray(0.1, 0.1, 0.1, 0.1);
        }

        if (scenario == "hidden")
        {
            foreground["visible"] = false;
        }

        var bridge = new ModalSelectorBridge(session.SessionId, native);
        var router = new UiInputRouter();
        router.ConfigureAccessibility(new ModalSelectorAccessibility(accessibility));

        using var target = router.BeginTargetScope(session.SessionId, "simulator-001");
        var selector = LiveUiSelector.Parse(new JsonObject { ["automationId"] = "settings-select-language-button" });

        var result = interaction
            ? await LiveUiTreeCapture.CaptureForInteractionAsync(session, bridge, router,
                "test-interaction", selector, RemoteAppToolIds.UiGetVisualTree, CancellationToken.None)
            : await LiveUiTreeCapture.CaptureForSelectorAsync(session, bridge, router,
                "ansight_tap_ui", null, selector, CancellationToken.None, targetSource: targetSource);

        Assert.True(allowed == result.IsSuccess, result.Message);

        if (!allowed)
        {
            Assert.Contains("different foreground accessibility control", result.Message);
        }
    }

    [Theory]
    [InlineData("ansight_find_ui")]
    [InlineData("ansight_wait_for_ui")]
    [InlineData("ansight_tap_ui")]
    public async Task NativeFallbackDoesNotBypassForegroundConflict(string toolName)
    {
        using var environment = new TestEnvironment();
        var state = new RuntimeState(new SessionCaptureStore(environment.ApplicationPaths));
        var sessionId = state.CreateSession("test.app", "Test App", IPAddress.Loopback, null, null);
        state.SetSessionDeviceProfile(sessionId, null,
            """{"device":{"nativeDeviceId":"simulator-001","osName":"iOS"}}""");

        var native = CreateModalNativeTree();
        var background = GetModalCopyNodes(native)[0];
        background["automationId"] = "background-button";
        background["label"] = "Change Language";

        var accessibility = CreateFlatModalAccessibility();
        Assert.True(VisualTreeTypeRegistry.TryCreateCompactV2(accessibility, out var registry, out var root));

        var modalMarker = Assert.Single(LiveUiNodeQuery.Find(root!, LiveUiSelector.Parse(new JsonObject
        {
            ["automationId"] = "PopoverDismissRegion"
        }), registry!)).Node;
        modalMarker.Parent!.AsArray().Remove(modalMarker);

        var bridge = new ModalSelectorBridge(sessionId, native);
        var composition = new MefHostComposition(environment.ApplicationPaths,
            new FileBackedEncryptedStorage(environment.SecureStorageFilePath));
        using var dispatcher = new OperationDispatcher(state, environment.ApplicationPaths,
            composition.Get<IKnownAppStore>(), composition.Get<IPairingConfigService>(), composition.Get<IPairingConfigCache>(),
            composition.Get<AppService>(), composition.Get<PairingService>(), composition.Get<DotNetProfilingService>(),
            composition.Get<NativeProfilingService>(), bridge);
        var input = new InputDriver();
        dispatcher.ConfigureUiInputDriver(input);
        dispatcher.ConfigureUiAccessibilityDriver(new ModalSelectorAccessibility(accessibility));

        var arguments = new JsonObject
        {
            ["sessionId"] = sessionId,
            ["automationId"] = "background-button",
            ["visible"] = true,
            ["enabled"] = true
        };

        if (toolName == "ansight_wait_for_ui")
        {
            arguments["condition"] = "visible";
            arguments["timeoutMs"] = 100;
        }

        if (toolName == "ansight_tap_ui")
        {
            arguments["includeScreenshot"] = false;
        }

        var result = await dispatcher.CallToolAsync(toolName, arguments);

        Assert.True(result.Payload?["isError"]?.GetValue<bool>(), result.Payload?.ToJsonString());
        Assert.Contains("different foreground accessibility control", result.Payload!.ToJsonString());
        Assert.Equal(0, input.Taps);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task NativeSheetScrollContainerRequiresVisibleChildCorroboration(bool hideSecondChild, bool allowed)
    {
        var session = CreateModalTestSession();
        var native = CreateModalNativeTree();
        var container = native["root"]!["children"]![0]!["children"]![0]!.AsObject();
        container["role"] = "scrollview";
        container["supportedActions"] = new JsonArray("scroll", "swipe");

        if (hideSecondChild)
        {
            GetModalCopyNodes(native)[1]["visible"] = false;
        }

        var bridge = new ModalSelectorBridge(session.SessionId, native);
        var router = new UiInputRouter();
        router.ConfigureAccessibility(new ModalSelectorAccessibility(CreateFlatModalAccessibility()));

        using var scope = router.BeginTargetScope(session.SessionId, "simulator-001");
        var selector = LiveUiSelector.Parse(new JsonObject
        {
            ["automationId"] = "area-share-options-list",
            ["action"] = "scroll",
            ["visible"] = true
        });

        var result = await LiveUiTreeCapture.CaptureForSelectorAsync(session, bridge, router,
            "ansight_scroll_ui", null, selector, CancellationToken.None,
            allowCached: false, targetSource: RemoteAppToolIds.UiGetVisualTree);

        Assert.True(result.IsSuccess, result.Message);

        var capture = Assert.IsType<LiveUiTreeCapture>(result.Capture);
        Assert.Equal(allowed, LiveUiNodeQuery.Find(capture.Root, selector, capture.TypeRegistry).Count > 0);
        Assert.Equal("window", bridge.LastRoot);
    }

    private static AppSessionSnapshot CreateModalTestSession() => new()
    {
        SessionId = "session-modal",
        AppId = "test.app",
        ClientName = "Test App",
        RemoteAddress = "127.0.0.1",
        CreatedUtc = DateTimeOffset.UtcNow,
        LastUpdatedUtc = DateTimeOffset.UtcNow,
        Status = "Connected",
        ConfigId = null,
        IsHistorical = false,
        MetricChannels = [],
        Metrics = []
    };

    private static JsonObject ModalCopySelector(int index = 0) => new()
    {
        ["automationId"] = "area-share-option-copy-button",
        ["ancestorAutomationId"] = "area-share-sheet",
        ["visible"] = true,
        ["enabled"] = true,
        ["index"] = index
    };

    private static JsonObject CreateFlatModalAccessibility()
        => DeviceAccessibilityTreeNormalizer.NormalizeIos(
            """
            <AppiumAUT>
              <XCUIElementTypeApplication type="XCUIElementTypeApplication" name="Test App" enabled="true" visible="true" x="0" y="0" width="400" height="800">
                <XCUIElementTypeButton type="XCUIElementTypeButton" name="PopoverDismissRegion" enabled="true" visible="true" x="0" y="0" width="400" height="400" />
                <XCUIElementTypeButton type="XCUIElementTypeButton" name="area-share-option-copy-button" label="Copy to Clipboard" enabled="true" visible="true" x="320" y="500" width="32" height="32" />
                <XCUIElementTypeButton type="XCUIElementTypeButton" name="area-share-option-copy-button" label="Copy to Clipboard" enabled="true" visible="true" x="320" y="610" width="32" height="32" />
              </XCUIElementTypeApplication>
            </AppiumAUT>
            """, viewportWidth: 400, viewportHeight: 800, maxNodes: 40, maxDepth: 16);

    private static JsonObject CreateModalNativeTree() => new()
    {
        ["format"] = "ansight.native.visual-tree.compact.v2",
        ["platform"] = "ios",
        ["types"] = new JsonArray("UIWindow", "UIView", "UIButton"),
        ["coordinateSpace"] = new JsonObject { ["x"] = 0, ["y"] = 0, ["width"] = 400, ["height"] = 800 },
        ["root"] = new JsonObject
        {
            ["id"] = "window",
            ["typeId"] = 0,
            ["visible"] = true,
            ["enabled"] = true,
            ["bounds"] = new JsonArray(0, 0, 400, 800),
            ["children"] = new JsonArray(new JsonObject
            {
                ["id"] = "sheet",
                ["typeId"] = 1,
                ["automationId"] = "area-share-sheet",
                ["visible"] = true,
                ["enabled"] = true,
                ["bounds"] = new JsonArray(0, 400, 400, 400),
                ["children"] = new JsonArray(new JsonObject
                {
                    ["id"] = "options",
                    ["typeId"] = 1,
                    ["automationId"] = "area-share-options-list",
                    ["visible"] = true,
                    ["enabled"] = true,
                    ["bounds"] = new JsonArray(0, 450, 400, 300),
                    ["children"] = new JsonArray(ModalNativeCopy("gps", 500), ModalNativeCopy("maps", 610))
                })
            })
        }
    };

    private static JsonObject ModalNativeCopy(string id, double y) => new()
    {
        ["id"] = id,
        ["typeId"] = 2,
        ["automationId"] = "area-share-option-copy-button",
        ["label"] = "Copy to Clipboard",
        ["role"] = "button",
        ["visible"] = true,
        ["enabled"] = true,
        ["bounds"] = new JsonArray(320, y, 32, 32)
    };

    private sealed class ModalSelectorAccessibility : IUiAccessibilityDriver
    {
        private readonly JsonObject payload;

        public ModalSelectorAccessibility(JsonObject payload)
        {
            this.payload = payload;
        }

        public Task<UiAccessibilityResult> CaptureAccessibilityAsync(UiAccessibilityRequest request,
            CancellationToken cancellationToken = default)
                    => Task.FromResult(new UiAccessibilityResult(true, "test", "Captured.", payload.DeepClone().AsObject()));
    }

    private sealed class ModalSelectorBridge : IAppToolBridge
    {
        private readonly string sessionId;
        private readonly JsonObject tree;

        public ModalSelectorBridge(string sessionId, JsonObject tree)
        {
            this.sessionId = sessionId;
            this.tree = tree;
        }

        public int Captures { get; private set; }
        public string ToolId { get; init; } = RemoteAppToolIds.UiGetVisualTree;
        public string? LastRoot { get; private set; }
        public bool IncludesComputedStyles { get; private set; }
        public bool FailCapture { get; init; }
        public JsonObject? Subtree { get; init; }
        public string? LastSubtreeRoot { get; private set; }
        public event EventHandler? ConnectionsChanged
        {
            add
            {
            }
            remove
            {
            }
        }

        public IReadOnlyList<string> GetConnectedSessionIds() => [sessionId];

        public bool IsSessionConnected(string id) => id == sessionId;

        public OperationResult ForceDisconnectSession(string id) => OperationResult.Failure("Unsupported.");

        public Task<AppToolBridgeResponse> QueryToolsAsync(string id, CancellationToken cancellationToken,
            AppToolBridgeRequestContext? requestContext = null)
            => Task.FromResult(AppToolBridgeResponse.FromSuccess("Catalog.", new ToolProtocolEnvelope
            {
                Type = ToolProtocolMessageTypes.CatalogType,
                Id = "catalog",
                SessionId = id,
                Payload = new JsonObject { ["tools"] = new JsonArray(new JsonObject { ["id"] = ToolId }) }
            }));

        public Task<AppToolBridgeResponse> CallToolAsync(string id, string toolId, JsonObject? arguments,
            CancellationToken cancellationToken, AppToolBridgeRequestContext? requestContext = null)
        {
            Assert.Equal(ToolId, toolId);
            LastRoot = arguments?["root"]?.GetValue<string>();
            IncludesComputedStyles = arguments?["includeComputedStyles"]?.GetValue<bool>() == true;
            LastSubtreeRoot = arguments?["rootNodeId"]?.GetValue<string>();
            Captures++;

            if (FailCapture)
            {
                return Task.FromResult(AppToolBridgeResponse.FromFailure("Native tree unavailable."));
            }

            return Task.FromResult(AppToolBridgeResponse.FromSuccess("Tree.", new ToolProtocolEnvelope
            {
                Type = ToolProtocolMessageTypes.ResultType,
                Id = "tree",
                SessionId = id,
                Payload = new JsonObject
                {
                    ["toolId"] = toolId,
                    ["success"] = true,
                    ["result"] = (LastSubtreeRoot is not null ? Subtree ?? tree : tree).DeepClone()
                }
            }));
        }
    }
}
