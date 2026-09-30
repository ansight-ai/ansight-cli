using Ansight.Host.Runtime.DeviceExecution;
using Ansight.Host.Runtime.Permissions;
using Ansight.Host.Tests.TestSupport;
using Ansight.Tools;

namespace Ansight.Host.Tests.Unit.Runtime;

public sealed class PermissionOperationTests
{
    [Fact]
    public async Task CatalogRegistersSessionBoundPermissionsAndQueriesDisconnectedSession()
    {
        using var environment = new TestEnvironment();
        var state = new RuntimeState(new SessionCaptureStore(environment.ApplicationPaths));
        var bridge = new TestAppToolBridge();
        var sessionId = state.CreateSession("com.example.app", "Example", IPAddress.Loopback, null, null);
        state.SetSessionDeviceProfile(sessionId, null, """{"device":{"nativeDeviceId":"physical-iphone"}}""");
        var router = new DevicePermissionsRouter();
        router.Configure(new NativePermissionService(_ => Task.FromResult(new DeviceInventory([], [
            new DeviceDescriptor("physical-iphone", "iPhone", "ios", "26", "Connected", true, true, "physical")
        ], [])), new NoCommands(), new RuntimeOptions()));
        var catalog = new ToolCatalog(state, environment.ApplicationPaths,
            new KnownAppStore(environment.ApplicationPaths), new EmptyPairingConfigService(), new EmptyPairingConfigCache(),
            bridge, null, new SessionResolver(state, bridge), devicePermissionsRouter: router);
        var names = catalog.BuildToolsListResult()["tools"]!.AsArray().OfType<JsonObject>().Select(tool => tool["name"]!.GetValue<string>()).ToHashSet();
        foreach (var suffix in new[] { "", "_ios", "_android" })
            foreach (var action in new[] { "grant", "revoke", "query" })
            {
                var name = $"ansight_{action}{suffix}_permission";
                Assert.Contains(name, names);
                Assert.True(DeviceExecutionTools.IsSupported(name));
            }
        Assert.Contains("ansight_reset_ios_permission", names);
        var response = await catalog.HandleToolsCallAsync(new JsonObject
        {
            ["name"] = "ansight_query_permission",
            ["arguments"] = new JsonObject { ["sessionId"] = sessionId, ["permission"] = "photos" }
        });
        Assert.False(response.Payload!["isError"]!.GetValue<bool>());
        var payload = response.Payload["structuredContent"]!;
        Assert.Equal("unsupported", payload["status"]!.GetValue<string>());
        Assert.Equal("com.example.app", payload["bundleIdentifier"]!.GetValue<string>());
        Assert.Equal("physical-iphone", payload["deviceId"]!.GetValue<string>());
        Assert.Equal(sessionId, payload["sessionId"]!.GetValue<string>());
        Assert.False(payload["supported"]!.GetValue<bool>());
    }

    private sealed class NoCommands : IDeviceCommandRunner
    {
        public Task<DeviceCommandResult> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken, int maximumBytes = 1_048_576)
            => throw new InvalidOperationException("Unsupported targets must not run native commands.");
    }
    private sealed class TestAppToolBridge : IAppToolBridge
    {
        public HashSet<string> ConnectedSessionIds { get; } = new(StringComparer.Ordinal);

        public event EventHandler? ConnectionsChanged
        {
            add { }
            remove { }
        }

        public IReadOnlyList<string> GetConnectedSessionIds() => ConnectedSessionIds.ToArray();

        public bool IsSessionConnected(string sessionId) => ConnectedSessionIds.Contains(sessionId);

        public OperationResult ForceDisconnectSession(string sessionId)
            => OperationResult.Failure("Not connected.");

        public Task<AppToolBridgeResponse> QueryToolsAsync(
            string sessionId,
            CancellationToken cancellationToken,
            AppToolBridgeRequestContext? requestContext = null)
            => Task.FromResult(AppToolBridgeResponse.FromFailure("No remote tools."));

        public Task<AppToolBridgeResponse> CallToolAsync(
            string sessionId,
            string toolId,
            JsonObject? arguments,
            CancellationToken cancellationToken,
            AppToolBridgeRequestContext? requestContext = null)
            => Task.FromResult(AppToolBridgeResponse.FromFailure("No remote tools."));
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
