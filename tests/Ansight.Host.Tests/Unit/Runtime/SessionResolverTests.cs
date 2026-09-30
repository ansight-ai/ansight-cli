using System.Text.Json.Nodes;
using Ansight.Host.Runtime.Operations;
using Ansight.Host.Tests.TestSupport;
using Ansight.Tools;

namespace Ansight.Host.Tests.Unit.Runtime;

public sealed partial class SessionResolverTests
{
    [Fact]
    public void TryResolveLiveSession_UsesAppAndDeviceToDisambiguateParallelSessions()
    {
        using var environment = new TestEnvironment();
        var runtimeState = new RuntimeState(new SessionCaptureStore(environment.ApplicationPaths));
        var firstSessionId = CreateSession(runtimeState, "com.example.app", "emulator-5554");
        var secondSessionId = CreateSession(runtimeState, "com.example.app", "physical-001");
        var resolver = new SessionResolver(
            runtimeState,
            new ConnectedAppToolBridge(firstSessionId, secondSessionId));

        var resolved = resolver.TryResolveLiveSession(
            new JsonObject
            {
                ["appId"] = "com.example.app",
                ["deviceId"] = "physical-001"
            },
            out var snapshot,
            out var error);

        Assert.True(resolved, error);
        Assert.Equal(secondSessionId, snapshot?.SessionId);
    }

    [Fact]
    public void TryResolveLiveSession_DeviceRequiresAppWhenSeveralAppsShareIt()
    {
        using var environment = new TestEnvironment();
        var runtimeState = new RuntimeState(new SessionCaptureStore(environment.ApplicationPaths));
        var firstSessionId = CreateSession(runtimeState, "com.example.first", "emulator-5554");
        var secondSessionId = CreateSession(runtimeState, "com.example.second", "emulator-5554");
        var resolver = new SessionResolver(
            runtimeState,
            new ConnectedAppToolBridge(firstSessionId, secondSessionId));

        var resolved = resolver.TryResolveLiveSession(
            new JsonObject { ["deviceId"] = "emulator-5554" },
            out var snapshot,
            out var error);

        Assert.False(resolved);
        Assert.Null(snapshot);
        Assert.Contains("Provide appId or sessionId", error, StringComparison.Ordinal);
    }

    [Fact]
    public void TryResolveWaitableSession_AllowsExplicitKnownSessionDuringReconnect()
    {
        using var environment = new TestEnvironment();
        var runtimeState = new RuntimeState(new SessionCaptureStore(environment.ApplicationPaths));
        var sessionId = runtimeState.CreateSession(
            "com.example.app",
            "Example App",
            IPAddress.Loopback,
            configId: null,
            processSessionId: null);
        var resolver = new SessionResolver(runtimeState, new DisconnectedAppToolBridge());

        var resolved = resolver.TryResolveWaitableSession(
            new JsonObject { ["sessionId"] = sessionId },
            out var snapshot,
            out var error);

        Assert.True(resolved, error);
        Assert.Equal(sessionId, snapshot?.SessionId);
    }

    [Fact]
    public void TryResolveWaitableSession_DoesNotGuessDisconnectedSessionFromAppId()
    {
        using var environment = new TestEnvironment();
        var runtimeState = new RuntimeState(new SessionCaptureStore(environment.ApplicationPaths));
        runtimeState.CreateSession(
            "com.example.app",
            "Example App",
            IPAddress.Loopback,
            configId: null,
            processSessionId: null);
        var resolver = new SessionResolver(runtimeState, new DisconnectedAppToolBridge());

        var resolved = resolver.TryResolveWaitableSession(
            new JsonObject { ["appId"] = "com.example.app" },
            out var snapshot,
            out var error);

        Assert.False(resolved);
        Assert.Null(snapshot);
        Assert.Contains("No live session", error, StringComparison.Ordinal);
    }

    private static string CreateSession(
        RuntimeState runtimeState,
        string appId,
        string deviceId)
    {
        var sessionId = runtimeState.CreateSession(
            appId,
            "Example App",
            IPAddress.Loopback,
            configId: null,
            processSessionId: null);
        runtimeState.SetSessionDeviceProfile(
            sessionId,
            profile: null,
            profileJson: $"{{\"device\":{{\"nativeDeviceId\":\"{deviceId}\"}}}}");
        return sessionId;
    }

    private sealed class ConnectedAppToolBridge(params string[] sessionIds) : IAppToolBridge
    {
        public event EventHandler? ConnectionsChanged
        {
            add { }
            remove { }
        }

        public IReadOnlyList<string> GetConnectedSessionIds()
            => sessionIds;

        public bool IsSessionConnected(string sessionId)
            => sessionIds.Contains(sessionId, StringComparer.Ordinal);

        public OperationResult ForceDisconnectSession(string sessionId)
            => OperationResult.Success("Disconnected.");

        public Task<AppToolBridgeResponse> QueryToolsAsync(
            string sessionId,
            CancellationToken cancellationToken,
            AppToolBridgeRequestContext? requestContext = null)
            => Task.FromResult(AppToolBridgeResponse.FromFailure("Not required."));

        public Task<AppToolBridgeResponse> CallToolAsync(
            string sessionId,
            string toolId,
            JsonObject? arguments,
            CancellationToken cancellationToken,
            AppToolBridgeRequestContext? requestContext = null)
            => Task.FromResult(AppToolBridgeResponse.FromFailure("Not required."));
    }

    private sealed class DisconnectedAppToolBridge : IAppToolBridge
    {
        public event EventHandler? ConnectionsChanged
        {
            add { }
            remove { }
        }

        public IReadOnlyList<string> GetConnectedSessionIds()
            => [];

        public bool IsSessionConnected(string sessionId)
            => false;

        public OperationResult ForceDisconnectSession(string sessionId)
            => OperationResult.Failure("The session is already disconnected.");

        public Task<AppToolBridgeResponse> QueryToolsAsync(
            string sessionId,
            CancellationToken cancellationToken,
            AppToolBridgeRequestContext? requestContext = null)
            => Task.FromResult(AppToolBridgeResponse.FromFailure("The session is disconnected."));

        public Task<AppToolBridgeResponse> CallToolAsync(
            string sessionId,
            string toolId,
            JsonObject? arguments,
            CancellationToken cancellationToken,
            AppToolBridgeRequestContext? requestContext = null)
            => Task.FromResult(AppToolBridgeResponse.FromFailure("The session is disconnected."));
    }
}
