using Ansight.Host.Tests.TestSupport;

namespace Ansight.Host.Tests.Unit.Runtime;

public sealed class KeyboardOperationTests
{
    [Fact]
    public async Task KeyboardOperations_OpenInspectAndDismissWithoutUnsafeBackInput()
    {
        using var environment = new TestEnvironment();
        var runtimeState = new RuntimeState(new SessionCaptureStore(environment.ApplicationPaths));
        var sessionId = runtimeState.CreateSession(
            "com.example.keyboard",
            "Keyboard App",
            IPAddress.Loopback,
            configId: null,
            processSessionId: null);
        runtimeState.SetSessionDeviceProfile(
            sessionId,
            profile: null,
            profileJson: """{"device":{"nativeDeviceId":"simulator-001"}}""");
        var bridge = new ConnectedAppToolBridge(sessionId);
        var driver = new KeyboardDriver();
        var inputRouter = new UiInputRouter();
        inputRouter.Configure(driver);
        inputRouter.ConfigureAccessibility(driver);
        var catalog = CreateToolCatalog(runtimeState, environment, bridge, inputRouter);

        var initialState = await CallAsync(catalog, "ansight_is_keyboard_open", sessionId);

        Assert.False(initialState["isOpen"]!.GetValue<bool>());
        Assert.Equal("deviceAccessibility", initialState["evidenceSource"]!.GetValue<string>());

        var opened = await CallAsync(
            catalog,
            "ansight_open_keyboard",
            sessionId,
            new JsonObject
            {
                ["automationId"] = "search-field",
                ["role"] = "textbox",
                ["includeScreenshot"] = false
            });

        Assert.True(opened["performed"]!.GetValue<bool>());
        Assert.True(opened["isOpen"]!.GetValue<bool>());
        Assert.Equal("keyboard.open", opened["capability"]!.GetValue<string>());
        Assert.Equal(1, driver.TapCount);

        var dismissed = await CallAsync(
            catalog,
            "ansight_dismiss_keyboard",
            sessionId,
            new JsonObject { ["includeScreenshot"] = false });

        Assert.True(dismissed["performed"]!.GetValue<bool>());
        Assert.False(dismissed["isOpen"]!.GetValue<bool>());
        Assert.Equal("keyboard.dismiss", dismissed["capability"]!.GetValue<string>());
        Assert.Equal(1, driver.BackCount);

        var alreadyDismissed = await CallAsync(
            catalog,
            "ansight_dismiss_keyboard",
            sessionId,
            new JsonObject { ["includeScreenshot"] = false });

        Assert.False(alreadyDismissed["performed"]!.GetValue<bool>());
        Assert.False(alreadyDismissed["isOpen"]!.GetValue<bool>());
        Assert.Equal(1, driver.BackCount);
    }

    private static ToolCatalog CreateToolCatalog(
        RuntimeState runtimeState,
        TestEnvironment environment,
        IAppToolBridge appToolBridge,
        UiInputRouter inputRouter)
        => new(
            runtimeState,
            environment.ApplicationPaths,
            new KnownAppStore(environment.ApplicationPaths),
            new EmptyPairingConfigService(),
            new EmptyPairingConfigCache(),
            appToolBridge,
            cloudSessionSharingService: null,
            new SessionResolver(runtimeState, appToolBridge),
            uiInputRouter: inputRouter);

    private static async Task<JsonObject> CallAsync(
        ToolCatalog catalog,
        string toolName,
        string sessionId,
        JsonObject? additionalArguments = null)
    {
        var arguments = additionalArguments?.DeepClone().AsObject() ?? new JsonObject();
        arguments["sessionId"] = sessionId;
        var response = await catalog.HandleToolsCallAsync(new JsonObject
        {
            ["name"] = toolName,
            ["arguments"] = arguments
        });

        Assert.False(response.IsError);
        Assert.NotNull(response.Payload);
        Assert.False(response.Payload!["isError"]!.GetValue<bool>());
        return Assert.IsType<JsonObject>(response.Payload["structuredContent"]);
    }

    private sealed class KeyboardDriver : IUiInputDriver, IUiAccessibilityDriver, IUiInputPreflightDriver
    {
        public int TapCount { get; private set; }

        public int BackCount { get; private set; }

        private bool IsOpen { get; set; }

        public UiInputAvailability GetAvailability(string deviceIdentifier)
            => new(true, "test-input", "Available.");

        public Task<UiInputResult> PrepareForInputAsync(
            UiInputPreflightRequest request,
            CancellationToken cancellationToken = default)
            => Task.FromResult(Success("Prepared."));

        public Task<UiInputResult> TapAsync(
            UiTapRequest request,
            CancellationToken cancellationToken = default)
        {
            TapCount++;
            IsOpen = true;
            return Task.FromResult(Success("Tapped text input."));
        }

        public Task<UiInputResult> SwipeAsync(
            UiSwipeRequest request,
            CancellationToken cancellationToken = default)
            => Task.FromResult(Success("Swiped."));

        public Task<UiInputResult> PinchAsync(
            UiPinchRequest request,
            CancellationToken cancellationToken = default)
            => Task.FromResult(Success("Pinched."));

        public Task<UiInputResult> TypeTextAsync(
            UiTextRequest request,
            CancellationToken cancellationToken = default)
            => Task.FromResult(Success("Typed."));

        public Task<UiInputResult> PressButtonAsync(
            UiButtonRequest request,
            CancellationToken cancellationToken = default)
        {
            Assert.Equal("back", request.Button);
            BackCount++;
            IsOpen = false;
            return Task.FromResult(Success("Dismissed keyboard."));
        }

        public Task<UiAccessibilityResult> CaptureAccessibilityAsync(
            UiAccessibilityRequest request,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new UiAccessibilityResult(
                true,
                "test-device-accessibility",
                "Captured.",
                CreateAccessibilityTree(IsOpen)));

        private static UiInputResult Success(string message)
            => new(true, "test-input", message);

        private static JsonObject CreateAccessibilityTree(bool keyboardVisible)
            => new()
            {
                ["format"] = "ansight.device-accessibility.compact.v2",
                ["platform"] = "ios",
                ["source"] = "device-accessibility",
                ["capturedAtUtc"] = DateTimeOffset.UtcNow,
                ["keyboardVisible"] = keyboardVisible,
                ["coordinateSpace"] = Bounds(0, 0, 1, 1),
                ["flagBits"] = new JsonObject
                {
                    ["visible"] = 1,
                    ["enabled"] = 2
                },
                ["types"] = new JsonArray("Application", "TextField"),
                ["root"] = new JsonObject
                {
                    ["typeId"] = 0,
                    ["role"] = "application",
                    ["flags"] = 3,
                    ["bounds"] = Bounds(0, 0, 1, 1),
                    ["children"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["typeId"] = 1,
                            ["automationId"] = "search-field",
                            ["role"] = "textbox",
                            ["flags"] = 3,
                            ["bounds"] = Bounds(0.1, 0.1, 0.8, 0.1),
                            ["supportedActions"] = new JsonArray("tap", "typeText", "focus"),
                            ["children"] = new JsonArray()
                        }
                    }
                },
                ["nodeCount"] = 2,
                ["truncated"] = false
            };

        private static JsonObject Bounds(double x, double y, double width, double height)
            => new()
            {
                ["x"] = x,
                ["y"] = y,
                ["width"] = width,
                ["height"] = height
            };
    }

    private sealed class ConnectedAppToolBridge(string sessionId) : IAppToolBridge
    {
        public event EventHandler? ConnectionsChanged
        {
            add { }
            remove { }
        }

        public IReadOnlyList<string> GetConnectedSessionIds() => [sessionId];

        public bool IsSessionConnected(string candidateSessionId)
            => string.Equals(sessionId, candidateSessionId, StringComparison.Ordinal);

        public OperationResult ForceDisconnectSession(string candidateSessionId)
            => OperationResult.Failure("Not supported by this test bridge.");

        public Task<AppToolBridgeResponse> QueryToolsAsync(
            string candidateSessionId,
            CancellationToken cancellationToken,
            AppToolBridgeRequestContext? requestContext = null)
            => Task.FromResult(AppToolBridgeResponse.FromFailure("No app tools are required."));

        public Task<AppToolBridgeResponse> CallToolAsync(
            string candidateSessionId,
            string toolId,
            JsonObject? arguments,
            CancellationToken cancellationToken,
            AppToolBridgeRequestContext? requestContext = null)
            => Task.FromResult(AppToolBridgeResponse.FromFailure("No app tools are required."));
    }

    private sealed class EmptyPairingConfigService : IPairingConfigService
    {
        public PairingIssueResult Issue(string appName, string appId, PairingConfigDuration duration)
            => throw new NotSupportedException();
    }

    private sealed class EmptyPairingConfigCache : IPairingConfigCache
    {
        public void Add(PairingConfig config)
        {
        }

        public void Add(CachedPairingConfig config)
        {
        }

        public IReadOnlyList<CachedPairingConfig> GetSnapshot() => [];

        public CachedPairingConfig? Find(string configId) => null;

        public bool Remove(string configId) => false;
    }
}
