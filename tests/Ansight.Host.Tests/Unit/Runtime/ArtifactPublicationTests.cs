using System.Buffers.Binary;
using System.Threading.Channels;
using Ansight.Host.Tests.TestSupport;

namespace Ansight.Host.Tests.Unit.Runtime;

public sealed class ArtifactPublicationTests
{
    [Fact]
    public async Task AppPublicationIsPersistedBeforeAcknowledgementAndSurvivesReload()
    {
        using var directory = TestDirectory.Create();
        var paths = new DataToolApplicationPaths(directory.Path);
        var store = new SessionCaptureStore(paths);
        var state = new RuntimeState(store);
        var sessionId = state.CreateSession("com.example.app", "Example", IPAddress.Loopback, null, null);
        var manager = new WebSocketSessionManager(state, new HostOperationTrafficLog(paths));
        using var socket = new ScriptedWebSocket();
        var session = manager.RunConnectedSessionAsync(sessionId, socket, new PairingSessionAuthorization(2, "grant-1", "read"));
        var id = Guid.NewGuid();
        try
        {
            socket.SendControl("begin", ArtifactPublishTransferManager.BeginAction, Descriptor(id, 4));
            Assert.True((await socket.ReadSentAsync())["success"]!.GetValue<bool>());
            socket.SendBinary(Frame(id, 1, 0, 0, [0x67, 0x6c, 0x54, 0x46]));
            socket.SendBinary(Frame(id, 2, 1, 4, []));
            socket.SendControl("commit", ArtifactPublishTransferManager.CommitAction, Identity(id));
            var receipt = await socket.ReadSentAsync();
            Assert.True(receipt["success"]!.GetValue<bool>(), receipt.ToJsonString());
            Assert.True(new SessionCaptureStore(paths).TryLoad(sessionId, out var retained));
            var artifact = Assert.Single(retained!.ArtifactSnapshots);
            Assert.Equal("model", artifact.Kind);
            Assert.Equal("Loaded model", artifact.Name);
            Assert.Equal("model/gltf-binary", Assert.Single(artifact.Entries).MimeType);
            Assert.Equal(artifact.SnapshotId, receipt["payload"]!["snapshotId"]!.GetValue<string>());
            var files = Directory.GetFiles(directory.Path, "model.glb", SearchOption.AllDirectories);
            Assert.NotEmpty(files);
            Assert.All(files, file => Assert.Equal(new byte[] { 0x67, 0x6c, 0x54, 0x46 }, File.ReadAllBytes(file)));
        }
        finally { socket.End(); await session.WaitAsync(TimeSpan.FromSeconds(5)); }
    }

    [Theory]
    [InlineData(2, 0, 0)] // early completion
    [InlineData(1, 1, 0)] // out of order
    [InlineData(1, 0, 1)] // wrong offset
    [InlineData(1, 0, 0)] // too many bytes
    public void InvalidTransfersCannotBeCommitted(byte type, int sequence, long offset)
    {
        using var manager = new ArtifactPublishTransferManager();
        var id = Guid.NewGuid();
        manager.Begin("one", Descriptor(id, 1));
        Assert.False(manager.TryHandleBinaryMessage("other", Frame(id, type, sequence, offset, [1, 2])));
        Assert.True(manager.TryHandleBinaryMessage("one", Frame(id, type, sequence, offset, [1, 2])));
        Assert.Throws<InvalidDataException>(() => manager.Commit("one", Identity(id), (_, _, _) => throw new Exception("Must not persist")));
    }

    [Fact]
    public void PublicationIsBoundedSessionScopedAndCancellationReleasesIt()
    {
        using var manager = new ArtifactPublishTransferManager();
        var id = Guid.NewGuid();
        Assert.Throws<InvalidDataException>(() => manager.Begin("one", Descriptor(id, ArtifactPublishTransferManager.MaximumSizeBytes + 1)));
        manager.Begin("one", Descriptor(id, 0));
        Assert.Throws<InvalidDataException>(() => manager.Begin("one", Descriptor(id, 0)));
        Assert.Throws<InvalidDataException>(() => manager.Commit("other", Identity(id), (_, _, _) => "bad"));
        manager.CancelSession("one");
        Assert.False(manager.TryHandleBinaryMessage("one", Frame(id, 2, 0, 0, [])));
        manager.Begin("one", Descriptor(id, 0));
        manager.TryHandleBinaryMessage("one", Frame(id, 2, 0, 0, []));
        Assert.Throws<IOException>(() => manager.Commit("one", Identity(id), (_, _, _) => null));
    }

    private static JsonObject Identity(Guid id) => new() { ["transferId"] = id.ToString("N") };
    private static JsonObject Descriptor(Guid id, long size) => new()
    {
        ["schema"] = ArtifactPublishTransferManager.Schema, ["transferId"] = id.ToString("N"),
        ["wireProtocol"] = "ansight.file-transfer.v1",
        ["artifact"] = new JsonObject
        {
            ["providerId"] = "scene", ["artifactId"] = "model", ["name"] = "Loaded model",
            ["kind"] = "model", ["mimeType"] = "model/gltf-binary", ["fileName"] = "model.glb", ["sizeBytes"] = size
        }
    };
    private static byte[] Frame(Guid id, byte type, int sequence, long offset, byte[] content)
    {
        var bytes = new byte[56 + content.Length];
        "ASFT"u8.CopyTo(bytes); bytes[4] = 1; bytes[5] = type;
        Encoding.ASCII.GetBytes(id.ToString("N")).CopyTo(bytes, 8);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(40), sequence);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(44), offset);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(52), content.Length);
        content.CopyTo(bytes, 56); return bytes;
    }

    private sealed class ScriptedWebSocket : WebSocket
    {
        private readonly Channel<Incoming?> incoming = Channel.CreateUnbounded<Incoming?>();
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

        public void Receive(JsonObject message) => incoming.Writer.TryWrite(new Incoming(Encoding.UTF8.GetBytes(message.ToJsonString()), WebSocketMessageType.Text));
        private sealed record Incoming(byte[] Bytes, WebSocketMessageType Type);
        public void SendBinary(byte[] bytes) => incoming.Writer.TryWrite(new Incoming(bytes, WebSocketMessageType.Binary));
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
            var bytes = message.Bytes;
            bytes.AsSpan().CopyTo(buffer.AsSpan());
            return new WebSocketReceiveResult(bytes.Length, message.Type, true);
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
