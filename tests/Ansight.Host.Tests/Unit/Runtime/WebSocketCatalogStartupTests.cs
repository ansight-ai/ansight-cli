using System.Threading.Channels;
using Ansight.Host.Tests.TestSupport;
using Ansight.Tools;

namespace Ansight.Host.Tests.Unit.Runtime;

public sealed class WebSocketCatalogStartupTests
{
    [Fact]
    public async Task StartupCatalogQuery_IsSentOnceAfterASuccessfulSessionOpenAcknowledgement()
    {
        using var directory = TestDirectory.Create();
        var paths = new DataToolApplicationPaths(directory.Path);
        var state = new RuntimeState(new SessionCaptureStore(paths));
        var sessionId = state.CreateSession("com.example.app", "Example", IPAddress.Loopback, null, null);
        var manager = new WebSocketSessionManager(state, new HostOperationTrafficLog(paths));
        using var socket = new ScriptedWebSocket();
        var session = manager.RunConnectedSessionAsync(sessionId, socket,
            new PairingSessionAuthorization(2, "grant-1", "read"));
        try
        {
            // A handshake alone, ordinary traffic, and a rejected open must not start discovery.
            socket.SendControl("log", PairingControlActions.ClientLog);
            await AssertControlResponseAsync(socket, "log", success: true);
            socket.SendControl("invalid-open", PairingControlActions.SessionOpen,
                new JsonObject { ["customProperties"] = false });
            await AssertControlResponseAsync(socket, "invalid-open", success: false);
            socket.SendControl("log-after-rejection", PairingControlActions.ClientLog);
            await AssertControlResponseAsync(socket, "log-after-rejection", success: true);

            socket.SendControl("open", PairingControlActions.SessionOpen);
            await AssertControlResponseAsync(socket, "open", success: true);
            var query = await socket.ReadSentAsync();
            Assert.Equal(ToolProtocolMessageTypes.QueryType, query["type"]?.GetValue<string>());
            Assert.Equal("index", query["payload"]?["detail"]?.GetValue<string>());
            socket.Receive(new JsonObject
            {
                ["type"] = ToolProtocolMessageTypes.CatalogType,
                ["id"] = "catalog-response",
                ["replyTo"] = query["id"]!.GetValue<string>(),
                ["sessionId"] = sessionId,
                ["capability"] = ToolProtocolMessageTypes.Capability,
                ["payload"] = new JsonObject
                {
                    ["schema"] = "ansight.tool-catalog.v3",
                    ["revision"] = "catalog-1",
                    ["detail"] = "index",
                    ["tools"] = new JsonArray()
                }
            });

            socket.SendControl("duplicate-open", PairingControlActions.SessionOpen);
            await AssertControlResponseAsync(socket, "duplicate-open", success: true);
            socket.SendControl("complete", PairingControlActions.SessionComplete);
            await AssertControlResponseAsync(socket, "complete", success: true);
            Assert.True(await session.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            socket.End();
            await session.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private static async Task AssertControlResponseAsync(ScriptedWebSocket socket, string replyTo, bool success)
    {
        var response = await socket.ReadSentAsync();
        Assert.Equal(PairingControlEnvelope.ResponseType, response["type"]?.GetValue<string>());
        Assert.Equal(replyTo, response["replyTo"]?.GetValue<string>());
        Assert.Equal(success, response["success"]?.GetValue<bool>());
    }

    private sealed class ScriptedWebSocket : WebSocket
    {
        private readonly Channel<string?> incoming = Channel.CreateUnbounded<string?>();
        private readonly Channel<JsonObject> outgoing = Channel.CreateUnbounded<JsonObject>();
        private WebSocketState state = WebSocketState.Open;

        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => state;
        public override string? SubProtocol => null;

        public void SendControl(string id, string action, JsonObject? payload = null)
            => Receive(new JsonObject
            {
                ["type"] = PairingControlEnvelope.RequestType,
                ["id"] = id,
                ["action"] = action,
                ["payload"] = payload
            });

        public void Receive(JsonObject message) => incoming.Writer.TryWrite(message.ToJsonString());
        public void End() => incoming.Writer.TryWrite(null);
        public Task<JsonObject> ReadSentAsync()
            => outgoing.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType,
            bool endOfMessage, CancellationToken cancellationToken)
        {
            outgoing.Writer.TryWrite(JsonNode.Parse(buffer.AsSpan())!.AsObject());
            return Task.CompletedTask;
        }

        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer,
            CancellationToken cancellationToken)
        {
            var message = await incoming.Reader.ReadAsync(cancellationToken);
            if (message is null)
            {
                state = WebSocketState.CloseReceived;
                return new WebSocketReceiveResult(0, WebSocketMessageType.Close, true);
            }
            var bytes = Encoding.UTF8.GetBytes(message);
            bytes.AsSpan().CopyTo(buffer.AsSpan());
            return new WebSocketReceiveResult(bytes.Length, WebSocketMessageType.Text, true);
        }

        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription,
            CancellationToken cancellationToken)
        {
            state = WebSocketState.Closed;
            return Task.CompletedTask;
        }

        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription,
            CancellationToken cancellationToken) => CloseAsync(closeStatus, statusDescription, cancellationToken);

        public override void Abort() => End();
        public override void Dispose()
        {
            state = WebSocketState.Closed;
            End();
        }
    }
}
