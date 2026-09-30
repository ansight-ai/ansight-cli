using Ansight.Host.Tests.TestSupport;
using Ansight.Host.UiAutomation;
using Ansight.Tools;

namespace Ansight.Host.Tests.Unit.Runtime;

public sealed class LiveVisualTreeCaptureServiceTests
{
    [Fact]
    public void Sources_IncludeOnlyPublishedTreesWithPlatformLabels()
    {
        var sources = LiveVisualTreeCaptureService.ResolveAppSources(
            CreateCatalog(VisualTreeContract.MauiToolId, VisualTreeContract.DomToolId,
                VisualTreeContract.NativeToolId, "files.list"), "ios");

        Assert.Equal(new[] { "MAUI", "DOM", "iOS tree" }, sources.Select(source => source.Label));
        Assert.Empty(LiveVisualTreeCaptureService.ResolveAppSources(CreateCatalog("files.list"), "android"));
    }

    [Theory]
    [InlineData(VisualTreeContract.MauiToolId, VisualTreeContract.MauiKind)]
    [InlineData(VisualTreeContract.DomToolId, VisualTreeContract.DomKind)]
    [InlineData(VisualTreeContract.ReactComponentToolId, VisualTreeContract.ReactComponentKind)]
    [InlineData(VisualTreeContract.ReactShadowToolId, VisualTreeContract.ReactShadowKind)]
    [InlineData(VisualTreeContract.NativeToolId, VisualTreeContract.NativeKind)]
    [InlineData(VisualTreeContract.FlutterToolId, VisualTreeContract.FlutterKind)]
    public async Task Capture_UsesSelectedProviderAndPersistsItsKind(string toolId, string kind)
    {
        using var environment = new TestEnvironment();
        var state = new RuntimeState(new SessionCaptureStore(environment.ApplicationPaths));
        var sessionId = CreateSession(state);
        var bridge = new TreeBridge(sessionId, VisualTreeContract.MauiToolId, toolId);
        var service = new LiveVisualTreeCaptureService(state, state, bridge);

        var result = await service.CaptureLiveVisualTreeAsync(sessionId, toolId);

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(toolId, bridge.LastToolId);
        Assert.True(state.TryGetSessionSnapshot(sessionId, out var snapshot));
        Assert.Equal(kind, Assert.Single(snapshot!.VisualTreeSnapshots).VisualTreeKind);
    }

    [Fact]
    public async Task Capture_RejectsUnavailableProviderWithoutFallingBack()
    {
        using var environment = new TestEnvironment();
        var state = new RuntimeState(new SessionCaptureStore(environment.ApplicationPaths));
        var sessionId = CreateSession(state);
        var bridge = new TreeBridge(sessionId, VisualTreeContract.MauiToolId);
        var service = new LiveVisualTreeCaptureService(state, state, bridge);

        var result = await service.CaptureLiveVisualTreeAsync(sessionId, VisualTreeContract.DomToolId);

        Assert.False(result.IsSuccess);
        Assert.Null(bridge.LastToolId);
    }

    [Fact]
    public async Task Accessibility_IsProbedWithoutSavingAndCapturedFreshWithoutAnAppCatalog()
    {
        using var environment = new TestEnvironment();
        var state = new RuntimeState(new SessionCaptureStore(environment.ApplicationPaths));
        var sessionId = CreateSession(state);
        var bridge = new TreeBridge(sessionId) { CatalogAvailable = false };
        var driver = new AccessibilityDriver();
        var service = new LiveVisualTreeCaptureService(state, state, bridge, driver);

        var sources = await service.GetSourcesAsync(sessionId);

        Assert.True(sources.IsSuccess);
        Assert.Equal(LiveUiTreeCapture.DeviceAccessibilityToolId, Assert.Single(sources.Sources).ToolId);
        Assert.True(state.TryGetSessionSnapshot(sessionId, out var before));
        Assert.Empty(before!.VisualTreeSnapshots);

        var result = await service.CaptureLiveVisualTreeAsync(sessionId, LiveUiTreeCapture.DeviceAccessibilityToolId);

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(2, driver.Requests.Count);
        Assert.All(driver.Requests, request => Assert.False(request.AllowCached));
        Assert.Equal(2000, driver.Requests[1].MaxNodes);
        Assert.True(state.TryGetSessionSnapshot(sessionId, out var after));
        Assert.Equal(VisualTreeContract.AccessibilityKind, Assert.Single(after!.VisualTreeSnapshots).VisualTreeKind);
        Assert.Null(bridge.LastToolId);
    }

    [Fact]
    public async Task Accessibility_IsOmittedWhenUnavailableAndNeverFallsBackToMaui()
    {
        using var environment = new TestEnvironment();
        var state = new RuntimeState(new SessionCaptureStore(environment.ApplicationPaths));
        var sessionId = CreateSession(state);
        var bridge = new TreeBridge(sessionId, VisualTreeContract.MauiToolId);
        var service = new LiveVisualTreeCaptureService(state, state, bridge);

        var sources = await service.GetSourcesAsync(sessionId);
        var result = await service.CaptureLiveVisualTreeAsync(sessionId, LiveUiTreeCapture.DeviceAccessibilityToolId);

        Assert.Equal(VisualTreeContract.MauiToolId, Assert.Single(sources.Sources).ToolId);
        Assert.False(result.IsSuccess);
        Assert.Null(bridge.LastToolId);
    }

    [Fact]
    public async Task ExternalSession_UsesAccessibilityWithoutSdkAndRejectsEndedSession()
    {
        using var environment = new TestEnvironment();
        var state = new RuntimeState(new SessionCaptureStore(environment.ApplicationPaths));
        var id = state.CreateDeviceSession(new WorkspaceTestTarget("ios", "simulator-001", "Phone", "test.app", false, false, true)
            { DeviceKind = DeviceKinds.Simulator, ExecutionMode = "device" });
        var bridge = new TreeBridge("unrelated-sdk-session");
        var driver = new AccessibilityDriver();
        var service = new LiveVisualTreeCaptureService(state, state, bridge, driver);
        var sources = await service.GetSourcesAsync(id);
        Assert.True(sources.IsSuccess, sources.Message);
        Assert.Equal(LiveUiTreeCapture.DeviceAccessibilityToolId, Assert.Single(sources.Sources).ToolId);
        Assert.True((await service.CaptureLiveVisualTreeAsync(id)).IsSuccess);
        Assert.False((await service.CaptureLiveVisualTreeAsync(id, VisualTreeContract.MauiToolId)).IsSuccess);
        Assert.Null(bridge.LastToolId);
        state.EndDeviceSession(id);
        Assert.False((await service.GetSourcesAsync(id)).IsSuccess);
        Assert.False((await service.CaptureLiveVisualTreeAsync(id)).IsSuccess);
    }

    private static string CreateSession(RuntimeState state)
    {
        var sessionId = state.CreateSession("test.app", "Test App", IPAddress.Loopback, null, null);
        state.SetSessionDeviceProfile(sessionId, null,
            """{"device":{"nativeDeviceId":"simulator-001","osName":"iOS"}}""");
        return sessionId;
    }

    private static JsonObject CreateCatalog(params string[] toolIds)
        => new() { ["tools"] = new JsonArray(toolIds.Select(toolId => (JsonNode)new JsonObject { ["id"] = toolId }).ToArray()) };

    private static JsonObject CreateTree(string format = VisualTreeContract.NativeFormat)
        => new()
        {
            ["format"] = format,
            ["platform"] = "ios",
            ["capturedAtUtc"] = DateTimeOffset.UtcNow,
            ["types"] = new JsonArray("View"),
            ["root"] = new JsonObject
            {
                ["id"] = "root", ["typeId"] = 0, ["children"] = new JsonArray()
            },
            ["nodeCount"] = 1
        };

    private sealed class AccessibilityDriver : IUiAccessibilityDriver
    {
        public List<UiAccessibilityRequest> Requests { get; } = [];

        public Task<UiAccessibilityResult> CaptureAccessibilityAsync(UiAccessibilityRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(new UiAccessibilityResult(true, "test", "Captured.", CreateTree(VisualTreeContract.DeviceAccessibilityFormat)));
        }
    }

    private sealed class TreeBridge(string sessionId, params string[] toolIds) : IAppToolBridge
    {
        public bool CatalogAvailable { get; init; } = true;
        public string? LastToolId { get; private set; }
        public event EventHandler? ConnectionsChanged { add { } remove { } }
        public IReadOnlyList<string> GetConnectedSessionIds() => [sessionId];
        public bool IsSessionConnected(string id) => id == sessionId;
        public OperationResult ForceDisconnectSession(string id) => OperationResult.Failure("Unsupported.");

        public Task<AppToolBridgeResponse> QueryToolsAsync(string id, CancellationToken cancellationToken, AppToolBridgeRequestContext? requestContext = null)
            => Task.FromResult(CatalogAvailable
                ? AppToolBridgeResponse.FromSuccess("Catalog.", new ToolProtocolEnvelope
                {
                    Type = ToolProtocolMessageTypes.CatalogType, Id = "catalog", SessionId = id, Payload = CreateCatalog(toolIds)
                })
                : AppToolBridgeResponse.FromFailure("Catalog unavailable."));

        public Task<AppToolBridgeResponse> CallToolAsync(string id, string toolId, JsonObject? arguments, CancellationToken cancellationToken, AppToolBridgeRequestContext? requestContext = null)
        {
            LastToolId = toolId;
            return Task.FromResult(AppToolBridgeResponse.FromSuccess("Captured.", new ToolProtocolEnvelope
            {
                Type = ToolProtocolMessageTypes.ResultType, Id = "capture", SessionId = id,
                Payload = new JsonObject { ["toolId"] = toolId, ["success"] = true, ["result"] = CreateTree() }
            }));
        }
    }
}
