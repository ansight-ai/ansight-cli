using Ansight.Host.Tests.TestSupport;
using Ansight.MacSimulatorHid;
using Ansight.Tools;

namespace Ansight.Host.Tests.Unit.Runtime;

public sealed class UiLongPressTests
{
    [Theory]
    [InlineData(false, 800)]
    [InlineData(true, 3500)]
    public async Task LongPressResolvesTargetsAndRetainsHoldDurationAndEvidence(bool coordinates, int duration)
    {
        using var fixture = new Fixture();
        var arguments = coordinates
            ? new JsonObject { ["normalizedX"] = 0.3, ["normalizedY"] = 0.6, ["durationMs"] = duration }
            : new JsonObject { ["automationId"] = "card" };
        var result = await fixture.Call("ansight_long_press_ui", arguments);
        Assert.False(result.Payload!["isError"]!.GetValue<bool>());
        var content = result.Payload["structuredContent"]!;
        Assert.True(content["performed"]!.GetValue<bool>());
        Assert.Equal("ui.long_press", content["capability"]!.GetValue<string>());
        var request = Assert.Single(fixture.Driver.Holds);
        Assert.Equal(duration, request.DurationMilliseconds);
        Assert.Equal(coordinates ? 0.3 : 0.5, request.NormalizedX, 6);
        Assert.Equal(coordinates ? 0.6 : 0.25, request.NormalizedY, 6);
        Assert.Equal(fixture.SessionId, request.SessionId);
        Assert.Equal("test.app", request.ApplicationIdentifier);
        Assert.Equal("simulator-001", request.DeviceIdentifier);
        Assert.True(fixture.State.TryGetSessionSnapshot(fixture.SessionId, out var snapshot));
        var recorded = Assert.Single(snapshot!.ApplicationEvents.Select(HostInputAction.Read).OfType<HostInputAction>());
        Assert.Equal("longPress", recorded.Kind);
        Assert.Equal(duration, recorded.DurationMilliseconds);
        Assert.Equal(content["actionId"]!.GetValue<string>(), recorded.EvidenceActionId);
        Assert.Contains(snapshot.VisualTreeSnapshots, tree => tree.ActionId == recorded.EvidenceActionId);
        var extracted = TimelineTaskExtractor.Extract(snapshot, snapshot.CreatedUtc, DateTimeOffset.UtcNow.AddSeconds(1),
            "Hold card", includeCoordinateFallbackTaps: true);
        Assert.Single(extracted.ReplaySteps);
        Assert.Contains("ansight_long_press_ui", extracted.ReplaySteps[0].Instruction);
        Assert.Contains($"\"durationMs\":{duration}", extracted.ReplaySteps[0].Instruction);
        if (!coordinates) Assert.Contains("ansight.ui.longPress", extracted.Source);
    }

    [Theory]
    [InlineData("{\"normalizedX\":0.2}")]
    [InlineData("{\"normalizedX\":-0.2,\"normalizedY\":0.3}")]
    [InlineData("{\"automationId\":\"card\",\"targetFingerprint\":\"stale\"}")]
    [InlineData("{\"automationId\":\"card\",\"durationMs\":499}")]
    [InlineData("{\"automationId\":\"card\",\"durationMs\":10001}")]
    [InlineData("{\"automationId\":\"card\",\"durationMs\":800.5}")]
    [InlineData("{\"automationId\":\"card\",\"durationMs\":\"800\"}")]
    [InlineData("{}")]
    [InlineData("{\"normalizedX\":null,\"normalizedY\":null}")]
    public async Task InvalidTargetsAndDurationsDoNotDeliverInput(string json)
    {
        using var fixture = new Fixture();
        var result = await fixture.Call("ansight_long_press_ui", JsonNode.Parse(json)!.AsObject());
        Assert.True(result.IsError || result.Payload?["isError"]?.GetValue<bool>() == true);
        Assert.Empty(fixture.Driver.Holds);
    }

    [Fact]
    public async Task SequencePreservesHoldDurationAndRejectsInvalidStepsBeforeAnyInput()
    {
        using var fixture = new Fixture();
        var arguments = new JsonObject
        {
            ["actions"] = new JsonArray(new JsonObject
            {
                ["kind"] = "longPress", ["normalizedX"] = 0.2, ["normalizedY"] = 0.7, ["durationMs"] = 2400
            }), ["settleMs"] = 0
        };
        var result = await fixture.Call("ansight_run_ui_sequence", arguments);
        Assert.False(result.Payload!["isError"]!.GetValue<bool>());
        Assert.Equal(2400, Assert.Single(fixture.Driver.Holds).DurationMilliseconds);
        var actions = arguments["actions"]!.AsArray();
        var invalid = actions[0]!.DeepClone();
        invalid["durationMs"] = 10001;
        actions.Add(invalid);
        result = await fixture.Call("ansight_run_ui_sequence", arguments);
        Assert.True(result.Payload!["isError"]!.GetValue<bool>());
        Assert.Single(fixture.Driver.Holds);
    }

    [Fact]
    public async Task SimulatorReleasesHeldPointerWhenCancelledAndSkipsAlreadyCancelledInput()
    {
        var phases = new List<MacSimulatorPointerPhase>();
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => HeadlessHostDeviceDriver.HoldSimulatorPointerAsync(
            phase => { phases.Add(phase); if (phase == MacSimulatorPointerPhase.Down) cancellation.Cancel(); },
            10_000, cancellation.Token));
        Assert.Equal([MacSimulatorPointerPhase.Down, MacSimulatorPointerPhase.Up], phases);
        phases.Clear();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => HeadlessHostDeviceDriver.HoldSimulatorPointerAsync(
            phases.Add, 800, cancellation.Token));
        Assert.Empty(phases);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly TestEnvironment environment = new();
        private readonly ToolCatalog catalog;
        public RuntimeState State { get; }
        public string SessionId { get; }
        public Driver Driver { get; } = new();

        public Fixture()
        {
            State = new RuntimeState(new SessionCaptureStore(environment.ApplicationPaths));
            SessionId = State.CreateSession("test.app", "Long press", IPAddress.Loopback, null, null);
            State.SetSessionDeviceProfile(SessionId, null, """{"device":{"nativeDeviceId":"simulator-001"}}""");
            var router = new UiInputRouter();
            router.Configure(Driver);
            router.ConfigureAccessibility(Driver);
            var bridge = new Bridge(SessionId);
            var composition = new MefHostComposition(environment.ApplicationPaths,
                new FileBackedEncryptedStorage(environment.SecureStorageFilePath));
            catalog = new ToolCatalog(State, environment.ApplicationPaths, composition.Get<IKnownAppStore>(),
                composition.Get<IPairingConfigService>(), composition.Get<IPairingConfigCache>(), bridge,
                cloudSessionSharingService: null, new SessionResolver(State, bridge), uiInputRouter: router);
        }

        public Task<RequestResult> Call(string name, JsonObject arguments)
        {
            arguments = arguments.DeepClone().AsObject();
            arguments["sessionId"] = SessionId;
            arguments["includeScreenshot"] = false;
            return catalog.HandleToolsCallAsync(new JsonObject { ["name"] = name, ["arguments"] = arguments });
        }

        public void Dispose() => environment.Dispose();
    }

    private sealed class Driver : IUiInputDriver, IUiAccessibilityDriver
    {
        public List<UiLongPressRequest> Holds { get; } = [];
        public UiInputAvailability GetAvailability(string deviceIdentifier) => new(true, "test", "Ready");
        public Task<UiInputResult> LongPressAsync(UiLongPressRequest request, CancellationToken cancellationToken = default)
        {
            Holds.Add(request);
            return Task.FromResult(new UiInputResult(true, "test", "Held"));
        }
        public Task<UiInputResult> TapAsync(UiTapRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<UiInputResult> SwipeAsync(UiSwipeRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<UiInputResult> PinchAsync(UiPinchRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<UiInputResult> TypeTextAsync(UiTextRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<UiInputResult> PressButtonAsync(UiButtonRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<UiAccessibilityResult> CaptureAccessibilityAsync(UiAccessibilityRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new UiAccessibilityResult(true, "test", "Captured", JsonNode.Parse("""
                {
                  "format":"ansight.device-accessibility.compact.v2", "platform":"ios", "source":"device-accessibility",
                  "coordinateSpace":{"x":0,"y":0,"width":1,"height":1}, "types":["Application","Button"],
                  "flagBits":{"visible":1,"enabled":2}, "nodeCount":2, "truncated":false,
                  "root":{"typeId":0,"role":"application","flags":3,"bounds":{"x":0,"y":0,"width":1,"height":1},
                    "children":[{"typeId":1,"automationId":"card","text":"Card","role":"button","flags":3,
                      "bounds":{"x":0.1,"y":0.2,"width":0.8,"height":0.1},"supportedActions":["tap","longPress"],"children":[]}]}
                }
                """)!.AsObject()));
    }

    private sealed class Bridge(string sessionId) : IAppToolBridge
    {
        public event EventHandler? ConnectionsChanged { add { } remove { } }
        public IReadOnlyList<string> GetConnectedSessionIds() => [sessionId];
        public bool IsSessionConnected(string candidateSessionId) => candidateSessionId == sessionId;
        public OperationResult ForceDisconnectSession(string candidateSessionId) => OperationResult.Failure("Unsupported");
        public Task<AppToolBridgeResponse> QueryToolsAsync(string candidateSessionId, CancellationToken cancellationToken,
            AppToolBridgeRequestContext? requestContext = null)
            => Task.FromResult(AppToolBridgeResponse.FromSuccess("Catalog", new ToolProtocolEnvelope
            {
                Type = ToolProtocolMessageTypes.CatalogType, Id = "catalog",
                Payload = new JsonObject { ["tools"] = new JsonArray() }
            }));
        public Task<AppToolBridgeResponse> CallToolAsync(string candidateSessionId, string toolId, JsonObject? arguments,
            CancellationToken cancellationToken, AppToolBridgeRequestContext? requestContext = null)
            => Task.FromResult(AppToolBridgeResponse.FromFailure("No app tools"));
    }
}
