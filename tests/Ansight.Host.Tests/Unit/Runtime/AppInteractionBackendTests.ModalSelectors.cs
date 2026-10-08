using Ansight.Host.Tests.TestSupport;
using Ansight.Tools;

namespace Ansight.Host.Tests.Unit.Runtime;

public sealed partial class AppInteractionBackendTests
{
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
            case "wrong-ancestor": arguments["ancestorAutomationId"] = "other-sheet"; break;
            case "wrong-index": arguments["index"] = 2; break;
            case "wrong-position": nativeCopy["bounds"] = new JsonArray(320, 100, 32, 32); break;
            case "wrong-id": accessibleCopy["automationId"] = "different-copy-button"; break;
            case "missing-bounds": nativeCopy.Remove("bounds"); break;
            case "hidden-accessibility": accessibleCopy["visible"] = false; break;
            case "disabled-accessibility": accessibleCopy["enabled"] = false; break;
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

    private static JsonObject[] GetModalCopyNodes(JsonObject payload)
    {
        Assert.True(VisualTreeTypeRegistry.TryCreateCompactV2(payload, out var types, out var root));
        return LiveUiNodeQuery.Find(root!, LiveUiSelector.Parse(new JsonObject
        {
            ["automationId"] = "area-share-option-copy-button"
        }), types!).Select(match => match.Node).ToArray();
    }

    private static AppSessionSnapshot CreateModalTestSession() => new()
    {
        SessionId = "session-modal", AppId = "test.app", ClientName = "Test App", RemoteAddress = "127.0.0.1",
        CreatedUtc = DateTimeOffset.UtcNow, LastUpdatedUtc = DateTimeOffset.UtcNow, Status = "Connected",
        ConfigId = null, IsHistorical = false, MetricChannels = [], Metrics = []
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
            ["id"] = "window", ["typeId"] = 0, ["visible"] = true, ["enabled"] = true,
            ["bounds"] = new JsonArray(0, 0, 400, 800),
            ["children"] = new JsonArray(new JsonObject
            {
                ["id"] = "sheet", ["typeId"] = 1, ["automationId"] = "area-share-sheet",
                ["visible"] = true, ["enabled"] = true, ["bounds"] = new JsonArray(0, 400, 400, 400),
                ["children"] = new JsonArray(new JsonObject
                {
                    ["id"] = "options", ["typeId"] = 1, ["automationId"] = "area-share-options-list",
                    ["visible"] = true, ["enabled"] = true, ["bounds"] = new JsonArray(0, 450, 400, 300),
                    ["children"] = new JsonArray(ModalNativeCopy("gps", 500), ModalNativeCopy("maps", 610))
                })
            })
        }
    };

    private static JsonObject ModalNativeCopy(string id, double y) => new()
    {
        ["id"] = id, ["typeId"] = 2, ["automationId"] = "area-share-option-copy-button",
        ["label"] = "Copy to Clipboard", ["role"] = "button", ["visible"] = true, ["enabled"] = true,
        ["bounds"] = new JsonArray(320, y, 32, 32)
    };

    private sealed class ModalSelectorAccessibility(JsonObject payload) : IUiAccessibilityDriver
    {
        public Task<UiAccessibilityResult> CaptureAccessibilityAsync(UiAccessibilityRequest request,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new UiAccessibilityResult(true, "test", "Captured.", payload.DeepClone().AsObject()));
    }

    private sealed class ModalSelectorBridge(string sessionId, JsonObject tree) : IAppToolBridge
    {
        public int Captures { get; private set; }
        public bool FailCapture { get; init; }
        public event EventHandler? ConnectionsChanged { add { } remove { } }
        public IReadOnlyList<string> GetConnectedSessionIds() => [sessionId];
        public bool IsSessionConnected(string id) => id == sessionId;
        public OperationResult ForceDisconnectSession(string id) => OperationResult.Failure("Unsupported.");
        public Task<AppToolBridgeResponse> QueryToolsAsync(string id, CancellationToken cancellationToken,
            AppToolBridgeRequestContext? requestContext = null)
            => Task.FromResult(AppToolBridgeResponse.FromSuccess("Catalog.", new ToolProtocolEnvelope
            {
                Type = ToolProtocolMessageTypes.CatalogType, Id = "catalog", SessionId = id,
                Payload = new JsonObject { ["tools"] = new JsonArray(new JsonObject { ["id"] = RemoteAppToolIds.UiGetVisualTree }) }
            }));
        public Task<AppToolBridgeResponse> CallToolAsync(string id, string toolId, JsonObject? arguments,
            CancellationToken cancellationToken, AppToolBridgeRequestContext? requestContext = null)
        {
            Assert.Equal(RemoteAppToolIds.UiGetVisualTree, toolId);
            Captures++;
            if (FailCapture) return Task.FromResult(AppToolBridgeResponse.FromFailure("Native tree unavailable."));
            return Task.FromResult(AppToolBridgeResponse.FromSuccess("Tree.", new ToolProtocolEnvelope
            {
                Type = ToolProtocolMessageTypes.ResultType, Id = "tree", SessionId = id,
                Payload = new JsonObject { ["toolId"] = toolId, ["success"] = true, ["result"] = tree.DeepClone() }
            }));
        }
    }
}
