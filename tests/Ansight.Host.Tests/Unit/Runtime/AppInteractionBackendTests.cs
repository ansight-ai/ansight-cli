using Ansight.Host.Tests.TestSupport;
using Ansight.Host.UiAutomation;
using Ansight.Tools;
using SkiaSharp;
using Ansight.Host.Runtime.Operations;
using Ansight.Host.Runtime.Tasks;

namespace Ansight.Host.Tests.Unit.Runtime;

public sealed partial class AppInteractionBackendTests
{
    [Theory]
    [InlineData("expect(input.label, { id: 'label', message: 'Input was supplied.' }).toEqual('private task input');", RepositoryTaskRunStatus.Passed)]
    [InlineData("expect(false, { id: 'failed', message: 'Expected failure.' }).toBe(true);", RepositoryTaskRunStatus.Failed)]
    [InlineData("", RepositoryTaskRunStatus.Inconclusive)]
    public async Task TasksUseExistingEngineWithFixedSessionAndAutomaticEvidence(string assertion, RepositoryTaskRunStatus expected)
    {
        using var environment = new TestEnvironment();
        var state = new RuntimeState(new SessionCaptureStore(environment.ApplicationPaths));
        var sessionId = state.CreateSession("test.app", "Test App", IPAddress.Loopback, null, null);
        state.SetSessionDeviceProfile(sessionId, null,
            """{"device":{"nativeDeviceId":"simulator-001","osName":"iOS"}}""");
        var screenshotPath = Path.Combine(environment.RootPath, "screenshot.png");
        using (var bitmap = new SKBitmap(30, 60))
        using (var image = SKImage.FromBitmap(bitmap))
        using (var data = image.Encode(SKEncodedImageFormat.Png, 100))
        using (var stream = File.Create(screenshotPath))
            data.SaveTo(stream);
        var tasksDirectory = Path.Combine(environment.RootPath, "ansight", "tasks");
        Directory.CreateDirectory(tasksDirectory);
        File.WriteAllText(Path.Combine(tasksDirectory, "verify.ts"), $$$"""
            export const task = {
              "schemaVersion": 1, "appId": "test.app", "title": "Verify input",
              "description": "Test interaction task routing.",
              "inputSchema": {"type":"object","properties":{"label":{"type":"string"}},"required":["label"],"additionalProperties":false},
              "timeoutSeconds": 10, "maximumActions": 4
            };
            export default async function run({ input, expect }) {
              {{{assertion}}}
              return { label: input.label };
            }
            """);
        var bridge = new ScreenshotBridge(sessionId, screenshotPath);
        var composition = new MefHostComposition(environment.ApplicationPaths,
            new FileBackedEncryptedStorage(environment.SecureStorageFilePath));
        using var dispatcher = new OperationDispatcher(state, environment.ApplicationPaths,
            composition.Get<IKnownAppStore>(), composition.Get<IPairingConfigService>(), composition.Get<IPairingConfigCache>(),
            composition.Get<AppService>(), composition.Get<PairingService>(), composition.Get<DotNetProfilingService>(),
            composition.Get<NativeProfilingService>(), bridge);
        dispatcher.ConfigureRepositoryTaskRuntime("node");
        dispatcher.ConfigureUiInputDriver(new InputDriver());
        var context = dispatcher.CreateAppInteractionContext(sessionId, environment.RootPath);
        await context.ExecuteAsync(new("ready", "snapshot"));
        var catalog = await context.ExecuteAsync(new("list", "tasks"));
        Assert.Equal("verify", Assert.Single(catalog.TaskCatalog!.Tasks).TaskId);
        var result = await context.ExecuteAsync(new("run", "task", TaskId: "verify",
            Input: new JsonObject { ["label"] = "private task input" }));

        Assert.Equal(expected, result.Task!.Status);
        Assert.Equal(expected == RepositoryTaskRunStatus.Passed, result.Succeeded);
        Assert.Equal(sessionId, result.Task.SessionId);
        Assert.Equal("test.app", result.Task.AppId);
        Assert.Equal(environment.RootPath, result.Task.RepositoryRootPath);
        Assert.NotNull(result.PreviousScreenshot);
        Assert.NotNull(result.Screenshot);
        Assert.Equal(2, bridge.FullCaptures);
        Assert.True(bridge.Probes > 2);
        Assert.Equal("unchanged", result.Screenshot!.Settling!.Status);
        Assert.True(state.TryGetSessionSnapshot(sessionId, out var snapshot));
        var events = snapshot!.ApplicationEvents.Where(item => item.EventType == "ansight.interaction").ToArray();
        Assert.Equal(2, events.Length);
        Assert.DoesNotContain(events, item => item.Details.Contains("private task input", StringComparison.Ordinal));
        var rejected = await context.ExecuteAsync(new("missing", "task", TaskId: "missing"));
        Assert.Equal(RepositoryTaskRunStatus.Rejected, rejected.Task!.Status);
        Assert.False(rejected.Succeeded);
        Assert.NotNull(rejected.Screenshot);
    }

    [Fact]
    public async Task RealBackendReturnsFreshTreesAndPersistsEvidenceWithoutReloadingCatalogs()
    {
        using var environment = new TestEnvironment();
        var state = new RuntimeState(new SessionCaptureStore(environment.ApplicationPaths));
        var sessionId = state.CreateSession("test.app", "Test App", IPAddress.Loopback, null, null);
        state.SetSessionDeviceProfile(sessionId, null,
            """{"device":{"nativeDeviceId":"simulator-001","osName":"iOS"}}""");
        var screenshotPath = Path.Combine(environment.RootPath, "screenshot.png");
        using (var bitmap = new SKBitmap(30, 60))
        using (var image = SKImage.FromBitmap(bitmap))
        using (var data = image.Encode(SKEncodedImageFormat.Png, 100))
        using (var stream = File.Create(screenshotPath))
            data.SaveTo(stream);
        var bridge = new ScreenshotBridge(sessionId, screenshotPath);
        var driver = new InputDriver();
        var router = new UiInputRouter();
        router.Configure(driver);
        router.ConfigureAccessibility(driver);
        var session = new AppInteractionContext(new AppInteractionBackend(
            state, environment.ApplicationPaths, bridge, router, sessionId));

        Assert.True((await session.ExecuteAsync(new("ready", "snapshot"))).Succeeded);
        Assert.True((await session.ExecuteAsync(new("1", "tap", 0.4, 0.6))).Succeeded);
        Assert.True((await session.ExecuteAsync(new("2", "type", Value: "private test text"))).Succeeded);

        Assert.Equal(3, bridge.FullCaptures);
        Assert.True(bridge.Probes > 3);
        Assert.Equal(1, driver.Preflights);
        Assert.Equal(1, driver.Taps);
        Assert.Equal("private test text", driver.TypedText);
        Assert.False(driver.ReplaceExisting);
        Assert.Equal(0.4, driver.LastTap!.NormalizedX);
        Assert.Equal(0.6, driver.LastTap.NormalizedY);
        Assert.Equal(3, driver.TreeCaptures);
        Assert.Equal(0, bridge.CatalogQueries);
        Assert.True(state.TryGetSessionSnapshot(sessionId, out var snapshot));
        Assert.Equal(3, snapshot!.Images.Count);
        Assert.Equal(3, snapshot.VisualTreeSnapshots.Count);
        var events = snapshot.ApplicationEvents.Where(item => item.EventType == "ansight.interaction").ToArray();
        Assert.Equal(3, events.Length);
        Assert.DoesNotContain(events, item => item.Details.Contains("private test text", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SemanticActionsResolveFreshTargetsAndReturnCompactTreesWithScreenshotFallback()
    {
        using var environment = new TestEnvironment();
        var state = new RuntimeState(new SessionCaptureStore(environment.ApplicationPaths));
        var sessionId = state.CreateSession("test.app", "Test App", IPAddress.Loopback, null, null);
        state.SetSessionDeviceProfile(sessionId, null,
            """{"device":{"nativeDeviceId":"simulator-001","osName":"iOS"}}""");
        var screenshotPath = Path.Combine(environment.RootPath, "screenshot.png");
        using (var bitmap = new SKBitmap(30, 60))
        using (var image = SKImage.FromBitmap(bitmap))
        using (var data = image.Encode(SKEncodedImageFormat.Png, 100))
        using (var stream = File.Create(screenshotPath)) data.SaveTo(stream);
        var bridge = new ScreenshotBridge(sessionId, screenshotPath);
        var driver = new InputDriver();
        var router = new UiInputRouter();
        router.Configure(driver);
        router.ConfigureAccessibility(driver);
        var context = new AppInteractionContext(new AppInteractionBackend(
            state, environment.ApplicationPaths, bridge, router, sessionId));
        var ready = await context.ExecuteAsync(new("ready", "snapshot"));
        Assert.Equal("available", ready.Ui!.Status);
        Assert.Equal("search-field", Assert.Single(ready.Ui.Nodes).AutomationId);
        Assert.NotNull(ready.Ui.SnapshotId);
        driver.TargetX = 0.65; // Move after observation: no cached coordinate may be used.
        var typed = await context.ExecuteAsync(new("type", "type", Value: "query",
            Target: new(AutomationId: "search-field")));
        Assert.True(typed.Succeeded);
        Assert.Equal(0.75, driver.LastTap!.NormalizedX, 5);
        Assert.True(driver.ReplaceExisting);
        Assert.Equal("query", driver.TypedText);
        Assert.Equal("query", Assert.Single(typed.Ui!.Nodes).Value);
        Assert.Equal(3, driver.TreeCaptures); // ready, fresh target resolution, after action
        driver.TargetAutomationId = new string('a', 400);
        driver.TargetText = new string('t', 400);
        var longTarget = await context.ExecuteAsync(new("long-target", "snapshot"));
        var observedTarget = Assert.Single(longTarget.Ui!.Nodes);
        Assert.Equal(driver.TargetAutomationId, observedTarget.AutomationId);
        Assert.Equal(driver.TargetText, observedTarget.Text);
        var tappedLongTarget = await context.ExecuteAsync(new("tap-long-target", "tap",
            Target: new(AutomationId: observedTarget.AutomationId, Text: observedTarget.Text)));
        Assert.True(tappedLongTarget.Succeeded);
        driver.TargetAutomationId = "search-field";
        driver.TargetText = "Search";
        var taps = driver.Taps;
        driver.DuplicateTarget = true;
        var ambiguous = await context.ExecuteAsync(new("ambiguous", "tap", Target: new(Text: "Search", Role: "textbox")));
        Assert.False(ambiguous.Succeeded);
        Assert.Equal(taps, driver.Taps);
        Assert.Contains("matched 2", ambiguous.Message);
        driver.TreeUnavailable = true;
        var unavailable = await context.ExecuteAsync(new("missing", "tap", Target: new(AutomationId: "search-field")));
        Assert.False(unavailable.Succeeded);
        Assert.Equal(taps, driver.Taps);
        Assert.Equal("unavailable", unavailable.Ui!.Status);
        Assert.NotNull(unavailable.Screenshot);
        Assert.True((await context.ExecuteAsync(new("coordinate", "tap", 0.4, 0.6))).Succeeded);
    }

    [Theory]
    [InlineData("42", null)]
    [InlineData("true", null)]
    [InlineData("\"retained\"", "retained")]
    public async Task InteractionTreesKeepTheirStringValueContractForScalarFrameworkValues(
        string capturedValue, string? expectedValue)
    {
        using var environment = new TestEnvironment();
        var driver = new InputDriver
        {
            CapturedValue = JsonNode.Parse(capturedValue),
            RootNodeId = "root-001",
            RootAutomationId = "SearchPage"
        };
        var tree = CreateInteractionTree(environment, driver);

        var capture = await tree.CaptureAsync("scalar-value", null, CancellationToken.None);

        Assert.NotNull(capture);
        Assert.Equal("available", tree.Latest!.Status);
        var node = Assert.Single(tree.Latest.Nodes, node => node.AutomationId == "search-field");
        Assert.Equal(expectedValue, node.Value);
        Assert.Equal("search-field", node.AutomationId);
        Assert.Equal("Search", node.Text);
        Assert.Equal("SearchPage", Assert.Single(node.AncestorAutomationIds));
    }

    [Fact]
    public async Task InteractionTreesBoundDescriptionsWithoutShorteningExactTargets()
    {
        using var environment = new TestEnvironment();
        var exactText = new string('t', 800);
        var driver = new InputDriver { TargetText = exactText, TargetCount = 40 };
        var tree = CreateInteractionTree(environment, driver);

        var capture = await tree.CaptureAsync("bounded-ui", null, CancellationToken.None);

        Assert.NotNull(capture);
        Assert.Equal("available", tree.Latest!.Status);
        Assert.True(tree.Latest.Truncated);
        Assert.NotEmpty(tree.Latest.Nodes);
        Assert.True(tree.Latest.Nodes.Count < driver.TargetCount);
        Assert.All(tree.Latest.Nodes, node => Assert.Equal(exactText, node.Text));
        Assert.True(JsonSerializer.Serialize(tree.Latest.Nodes).Length <= UiProjectionOptions.Interaction.MaximumCharacters);
    }

    private static AppInteractionTree CreateInteractionTree(TestEnvironment environment, InputDriver driver)
    {
        var state = new RuntimeState(new SessionCaptureStore(environment.ApplicationPaths));
        var sessionId = state.CreateSession("test.app", "Test App", IPAddress.Loopback, null, null);
        state.SetSessionDeviceProfile(sessionId, null,
            """{"device":{"nativeDeviceId":"simulator-001","osName":"iOS"}}""");
        Assert.True(state.TryGetSessionSnapshot(sessionId, out var session));
        var router = new UiInputRouter();
        router.Configure(driver);
        router.ConfigureAccessibility(driver);
        return new AppInteractionTree(state, new ScreenshotBridge(sessionId, string.Empty), router, session!);
    }

    private sealed class ScreenshotBridge(string sessionId, string path) : IAppToolBridge
    {
        public int Captures { get; private set; }
        public int FullCaptures { get; private set; }
        public int Probes { get; private set; }
        public int CatalogQueries { get; private set; }
        public event EventHandler? ConnectionsChanged { add { } remove { } }
        public IReadOnlyList<string> GetConnectedSessionIds() => [sessionId];
        public bool IsSessionConnected(string id) => id == sessionId;
        public OperationResult ForceDisconnectSession(string id) => throw new NotSupportedException();
        public Task<AppToolBridgeResponse> QueryToolsAsync(string id, CancellationToken cancellationToken,
            AppToolBridgeRequestContext? requestContext = null)
        {
            CatalogQueries++;
            return Task.FromResult(AppToolBridgeResponse.FromFailure("No SDK tree available."));
        }
        public Task<AppToolBridgeResponse> CallToolAsync(string id, string toolId, JsonObject? arguments,
            CancellationToken cancellationToken, AppToolBridgeRequestContext? requestContext = null)
        {
            Assert.Equal(sessionId, id);
            Assert.Equal(RemoteAppToolIds.UiGetScreenshot, toolId);
            Assert.True(arguments!["afterScreenUpdates"]!.GetValue<bool>());
            Captures++;
            if (arguments["maxWidth"]!.GetValue<int>() == 1024) FullCaptures++;
            else
            {
                Assert.Equal(256, arguments["maxWidth"]!.GetValue<int>());
                Probes++;
            }
            return Task.FromResult(AppToolBridgeResponse.FromSuccess("Captured.", new ToolProtocolEnvelope
            {
                Type = ToolProtocolMessageTypes.ResultType, Id = "capture", SessionId = id,
                Payload = new JsonObject
                {
                    ["toolId"] = toolId, ["success"] = true,
                    ["result"] = new JsonObject { ["artifactPath"] = path, ["format"] = "png", ["capturedAtUtc"] = DateTimeOffset.UtcNow }
                }
            }));
        }
    }

    private sealed class InputDriver : IUiInputDriver, IUiInputPreflightDriver, IUiAccessibilityDriver
    {
        public int Preflights { get; private set; }
        public int Taps { get; private set; }
        public string? TypedText { get; private set; }
        public bool ReplaceExisting { get; private set; }
        public UiTapRequest? LastTap { get; private set; }
        public int TreeCaptures { get; private set; }
        public double TargetX { get; set; } = 0.3;
        public bool DuplicateTarget { get; set; }
        public bool TreeUnavailable { get; set; }
        public string TargetAutomationId { get; set; } = "search-field";
        public string TargetText { get; set; } = "Search";
        public int TargetCount { get; set; } = 1;
        public JsonNode? CapturedValue { get; set; }
        public string? RootNodeId { get; set; }
        public string? RootAutomationId { get; set; }
        public UiInputAvailability GetAvailability(string deviceIdentifier) => new(true, "test", "Ready.");
        public Task<UiInputResult> PrepareForInputAsync(UiInputPreflightRequest request, CancellationToken cancellationToken = default)
        {
            Preflights++;
            return Task.FromResult(new UiInputResult(true, "test", "Ready."));
        }
        public Task<UiInputResult> TapAsync(UiTapRequest request, CancellationToken cancellationToken = default)
        {
            Assert.Equal("simulator-001", request.DeviceIdentifier);
            Assert.Equal("test.app", request.ApplicationIdentifier);
            LastTap = request;
            Taps++;
            return Task.FromResult(new UiInputResult(true, "test", "Tapped."));
        }
        public Task<UiInputResult> TypeTextAsync(UiTextRequest request, CancellationToken cancellationToken = default)
        {
            ReplaceExisting = request.ReplaceExisting;
            TypedText = request.Text;
            return Task.FromResult(new UiInputResult(true, "test", "Typed."));
        }
        public Task<UiInputResult> SwipeAsync(UiSwipeRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<UiInputResult> PinchAsync(UiPinchRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<UiInputResult> PressButtonAsync(UiButtonRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<UiAccessibilityResult> CaptureAccessibilityAsync(UiAccessibilityRequest request, CancellationToken cancellationToken = default)
        {
            Assert.False(request.AllowCached);
            TreeCaptures++;
            if (TreeUnavailable) return Task.FromResult(new UiAccessibilityResult(false, "test", "Tree unavailable.", null));
            var target = new JsonObject
            {
                ["typeId"] = 1, ["automationId"] = TargetAutomationId, ["role"] = "textbox",
                ["text"] = TargetText, ["value"] = CapturedValue?.DeepClone() ?? JsonValue.Create(TypedText), ["flags"] = 3,
                ["bounds"] = new JsonArray(TargetX, 0.55, 0.2, 0.1),
                ["supportedActions"] = new JsonArray("tap", "typeText")
            };
            var children = new JsonArray(target);
            for (var index = 1; index < TargetCount; index++)
            {
                var additional = target.DeepClone().AsObject();
                additional["automationId"] = $"{TargetAutomationId}-{index}";
                children.Add(additional);
            }
            if (DuplicateTarget) children.Add(target.DeepClone());
            return Task.FromResult(new UiAccessibilityResult(true, "test", "Captured.", new JsonObject
            {
                ["format"] = "ansight.device-accessibility.compact.v2",
                ["types"] = new JsonArray("Application", "TextField"),
                ["coordinateSpace"] = new JsonObject { ["x"] = 0, ["y"] = 0, ["width"] = 1, ["height"] = 1 },
                ["root"] = new JsonObject
                {
                    ["id"] = RootNodeId, ["automationId"] = RootAutomationId,
                    ["typeId"] = 0, ["flags"] = 3, ["bounds"] = new JsonArray(0, 0, 1, 1), ["children"] = children
                }
            }));
        }
    }
}
