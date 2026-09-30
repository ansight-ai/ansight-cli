using Ansight.Host.Tests.TestSupport;
using Ansight.Tools;

namespace Ansight.Host.Tests.Unit.Runtime;

public sealed class HostOperationContractTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CallAppTool_PreservesLegacyDefaultsAlongsideHostDefaultsOnSuccessAndFailure(bool success)
    {
        using var environment = new TestEnvironment();
        var state = new RuntimeState(new SessionCaptureStore(environment.ApplicationPaths));
        var sessionId = state.CreateSession("com.example.app", "Example", IPAddress.Loopback, null, null);
        var bridge = new RecordingAppToolBridge(sessionId, success);
        var catalog = CreateCatalog(environment, state, bridge);

        var response = await catalog.HandleToolsCallAsync(new JsonObject
        {
            ["name"] = "ansight_call_app_tool",
            ["arguments"] = new JsonObject
            {
                ["sessionId"] = sessionId,
                ["toolId"] = "ui.get_screenshot",
                ["arguments"] = new JsonObject { ["quality"] = 55 }
            }
        });

        Assert.False(response.IsError);
        Assert.Equal(!success, response.Payload!["isError"]!.GetValue<bool>());
        var payload = response.Payload["structuredContent"]!.AsObject();
        var defaults = payload["appliedHostDefaults"]!.AsObject();
        Assert.Equal("jpeg", defaults["format"]!.GetValue<string>());
        Assert.Equal(55, bridge.LastArguments!["quality"]!.GetValue<int>());
        Assert.False(defaults.ContainsKey("quality"));
    }

    [Fact]
    public async Task GetFocusedControl_WithoutClientSelectionProvidesLiveDiscoveryGuidance()
    {
        using var environment = new TestEnvironment();
        var state = new RuntimeState(new SessionCaptureStore(environment.ApplicationPaths));
        var catalog = CreateCatalog(environment, state, new RecordingAppToolBridge("session", true));

        var response = await catalog.HandleToolsCallAsync(new JsonObject
        {
            ["name"] = "ansight_get_focused_control",
            ["arguments"] = new JsonObject()
        });

        Assert.True(response.Payload!["isError"]!.GetValue<bool>());
        Assert.Contains("ansight_find_ui", response.Payload["structuredContent"]!["message"]!.GetValue<string>());
    }

    private static ToolCatalog CreateCatalog(TestEnvironment environment, RuntimeState state, IAppToolBridge bridge)
        => new(
            state,
            environment.ApplicationPaths,
            new KnownAppStore(environment.ApplicationPaths),
            new EmptyPairingConfigService(),
            new EmptyPairingConfigCache(),
            bridge,
            cloudSessionSharingService: null,
            new SessionResolver(state, bridge));

    private sealed class RecordingAppToolBridge(string sessionId, bool success) : IAppToolBridge
    {
        public JsonObject? LastArguments { get; private set; }

        public event EventHandler? ConnectionsChanged { add { } remove { } }

        public IReadOnlyList<string> GetConnectedSessionIds() => [sessionId];

        public bool IsSessionConnected(string candidate) => candidate == sessionId;

        public OperationResult ForceDisconnectSession(string candidate) => OperationResult.Failure("Unsupported.");

        public Task<AppToolBridgeResponse> QueryToolsAsync(
            string candidate, CancellationToken cancellationToken, AppToolBridgeRequestContext? requestContext = null)
            => Task.FromResult(AppToolBridgeResponse.FromFailure("No catalog."));

        public Task<AppToolBridgeResponse> CallToolAsync(
            string candidate, string toolId, JsonObject? arguments, CancellationToken cancellationToken,
            AppToolBridgeRequestContext? requestContext = null)
        {
            LastArguments = arguments?.DeepClone().AsObject();
            return Task.FromResult(success
                ? AppToolBridgeResponse.FromSuccess("OK", new ToolProtocolEnvelope
                {
                    Type = ToolProtocolMessageTypes.ResultType,
                    Id = "test-response",
                    SessionId = candidate,
                    Payload = new JsonObject { ["toolId"] = toolId, ["success"] = true }
                })
                : AppToolBridgeResponse.FromFailure("Device unavailable."));
        }
    }

    private sealed class EmptyPairingConfigService : IPairingConfigService
    {
        public PairingIssueResult Issue(string appName, string appId, PairingConfigDuration duration)
            => throw new NotSupportedException();
    }

    private sealed class EmptyPairingConfigCache : IPairingConfigCache
    {
        public void Add(PairingConfig config) { }
        public void Add(CachedPairingConfig config) { }
        public IReadOnlyList<CachedPairingConfig> GetSnapshot() => [];
        public CachedPairingConfig? Find(string configId) => null;
        public bool Remove(string configId) => false;
    }
}
