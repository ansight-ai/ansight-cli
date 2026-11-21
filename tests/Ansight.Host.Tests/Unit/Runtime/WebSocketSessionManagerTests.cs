using System.Text.Json.Nodes;
using Ansight.Host.Runtime.WebSocketSessions;

namespace Ansight.Host.Tests.Unit.Runtime;

public sealed class WebSocketSessionManagerTests
{
    [Fact]
    public void LatePairingFailure_DoesNotFinalizeAnActiveReplacementConnection()
    {
        using var environment = new TestSupport.TestEnvironment();
        var state = new RuntimeState(new SessionCaptureStore(environment.ApplicationPaths));
        var id = state.CreateSession("app", "Client", IPAddress.Loopback, null, "process");
        var registry = new SessionConnectionRegistry();
        using var socket = new System.Net.WebSockets.ClientWebSocket();
        var connection = new LiveSessionConnection(id, socket,
            new PairingSessionAuthorization(2, "grant", "read"));
        registry.Register(id, connection);
        state.SetSessionStatus(id, "WebSocket Open");

        Assert.False(registry.RunWhenDisconnected(id,
            () => state.SetSessionStatus(id, "WebSocket Timeout")));
        Assert.True(state.TryGetSessionSnapshot(id, out var live));
        Assert.False(live!.IsHistorical);
        Assert.Equal("WebSocket Open", live.Status);

        Assert.True(registry.Unregister(id, connection));
        Assert.True(registry.RunWhenDisconnected(id,
            () => state.SetSessionStatus(id, "WebSocket Timeout")));
        Assert.True(state.TryGetSessionSnapshot(id, out var ended));
        Assert.True(ended!.IsHistorical);
        state.SetSessionStatus(id, "WebSocket Open");
        Assert.True(state.TryGetSessionSnapshot(id, out var resumed));
        Assert.False(resumed!.IsHistorical);
    }

    [Fact]
    public void CreateCallToolEnvelope_ClonesCallerOwnedArgumentsAndAfterEvidence()
    {
        var arguments = new JsonObject
        {
            ["includeHidden"] = false
        };
        var after = new JsonObject
        {
            ["include"] = new JsonArray("visualTree")
        };
        var owner = new JsonObject
        {
            ["arguments"] = arguments,
            ["after"] = after
        };

        var envelope = WebSocketSessionManager.CreateCallToolEnvelope(
            "session-123",
            "redpoint.3d_player.list_views",
            arguments,
            after);

        var payload = Assert.IsType<JsonObject>(envelope.Payload);
        var clonedArguments = Assert.IsType<JsonObject>(payload["arguments"]);
        var clonedAfter = Assert.IsType<JsonObject>(payload["after"]);
        Assert.NotSame(arguments, clonedArguments);
        Assert.NotSame(after, clonedAfter);
        Assert.Same(owner, arguments.Parent);
        Assert.Same(owner, after.Parent);
        Assert.False(clonedArguments["includeHidden"]?.GetValue<bool>());
        Assert.Equal("visualTree", clonedAfter["include"]?[0]?.GetValue<string>());
    }
}
