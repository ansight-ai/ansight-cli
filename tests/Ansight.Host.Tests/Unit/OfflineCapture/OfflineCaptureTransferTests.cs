using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Ansight.Host.Runtime.OfflineCapture;

namespace Ansight.Host.Tests.Unit.OfflineCapture;

public sealed class OfflineCaptureTransferTests
{
    [Fact]
    public void ReceiptFollowsPersistedImportAndRetryAfterRestartDoesNotDuplicate()
    {
        using var environment = new TestSupport.TestEnvironment();
        var store = new SessionCaptureStore(environment.ApplicationPaths);
        var state = new RuntimeState(store);
        var live = state.CreateSession("app.test", "App", IPAddress.Loopback, null, null);
        var source = Guid.NewGuid().ToString("D");
        var archive = Archive(source, "app.test");
        var descriptor = Descriptor(source, archive);
        using (var receiver = new OfflineCaptureTransferManager(state))
        {
            Deliver(receiver, live, descriptor, archive);
            var receipt = receiver.Commit(live, descriptor);
            Assert.True(receipt["durable"]!.GetValue<bool>());
            Assert.Equal("offline-" + source, receipt["importedSessionId"]!.GetValue<string>());
        }
        var restarted = new RuntimeState(new SessionCaptureStore(environment.ApplicationPaths));
        Assert.True(restarted.TryGetSessionSnapshot("offline-" + source, out var imported));
        Assert.Single(imported!.Metrics);
        Assert.Single(imported.Images);
        Assert.Single(imported.ApplicationEvents);
        Assert.Single(imported.VisualTreeSnapshots);
        Assert.Single(imported.Touches);
        Assert.Single(imported.NetworkRequests);
        Assert.Contains(imported.Logs, log => log.Message == "Offline log");
        var nextLive = restarted.CreateSession("app.test", "App", IPAddress.Loopback, null, null);
        using var retry = new OfflineCaptureTransferManager(restarted);
        descriptor["transferId"] = Guid.NewGuid().ToString("N");
        Deliver(retry, nextLive, descriptor, archive);
        var repeated = retry.Commit(nextLive, descriptor);
        Assert.Equal("offline-" + source, repeated["importedSessionId"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("hash")]
    [InlineData("identity")]
    [InlineData("app")]
    [InlineData("incomplete")]
    [InlineData("unsafe")]
    public void InvalidTransferNeverProducesReceipt(string invalid)
    {
        using var environment = new TestSupport.TestEnvironment();
        var state = new RuntimeState(new SessionCaptureStore(environment.ApplicationPaths));
        var live = state.CreateSession("app.test", "App", IPAddress.Loopback, null, null);
        var source = Guid.NewGuid().ToString("D");
        var archive = Archive(invalid == "identity" ? Guid.NewGuid().ToString("D") : source, invalid == "app" ? "other.app" : "app.test", invalid == "unsafe");
        var descriptor = Descriptor(source, archive);
        if (invalid == "hash") descriptor["sha256"] = new string('a', 64);
        using var receiver = new OfflineCaptureTransferManager(state);
        if (invalid == "incomplete") receiver.Begin(live, descriptor);
        else Deliver(receiver, live, descriptor, archive);
        Assert.Throws<InvalidDataException>(() => receiver.Commit(live, descriptor));
        Assert.False(state.TryGetSessionSnapshot("offline-" + source, out _));
    }

    [Fact]
    public void OutOfOrderFrameInvalidatesTransferAndDisconnectCancelsPendingImport()
    {
        using var environment = new TestSupport.TestEnvironment();
        var state = new RuntimeState(new SessionCaptureStore(environment.ApplicationPaths));
        var live = state.CreateSession("app.test", "App", IPAddress.Loopback, null, null);
        var archive = Archive(Guid.NewGuid().ToString("D"), "app.test");
        var descriptor = Descriptor(Guid.NewGuid().ToString("D"), archive);
        using var receiver = new OfflineCaptureTransferManager(state);
        receiver.Begin(live, descriptor);
        Assert.Throws<InvalidDataException>(() => receiver.TryHandleBinaryMessage(live, Frame(descriptor, 1, 1, 0, archive)));
        Assert.Throws<InvalidDataException>(() => receiver.Commit(live, descriptor));
        receiver.Begin(live, descriptor);
        receiver.CancelSession(live);
        Assert.Throws<InvalidDataException>(() => receiver.Commit(live, descriptor));
    }

    private static JsonObject Descriptor(string source, byte[] bytes) => new()
    {
        ["schema"] = "ansight.offline-transfer.v1", ["sessionId"] = source,
        ["transferId"] = Guid.NewGuid().ToString("N"), ["byteCount"] = bytes.LongLength,
        ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(bytes))
    };

    private static void Deliver(OfflineCaptureTransferManager receiver, string live, JsonObject descriptor, byte[] bytes)
    {
        receiver.Begin(live, descriptor);
        Assert.True(receiver.TryHandleBinaryMessage(live, Frame(descriptor, 1, 0, 0, bytes)));
        Assert.True(receiver.TryHandleBinaryMessage(live, Frame(descriptor, 2, 1, bytes.Length, [])));
    }

    private static byte[] Frame(JsonObject descriptor, byte type, int sequence, long offset, byte[] bytes)
    {
        var frame = new byte[56 + bytes.Length];
        "ASFT"u8.CopyTo(frame);
        frame[4] = 1;
        frame[5] = type;
        Encoding.ASCII.GetBytes(descriptor["transferId"]!.GetValue<string>()).CopyTo(frame, 8);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(40), sequence);
        BinaryPrimitives.WriteInt64LittleEndian(frame.AsSpan(44), offset);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(52), bytes.Length);
        bytes.CopyTo(frame, 56);
        return frame;
    }

    private static byte[] Archive(string source, string appId, bool unsafePath = false)
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            if (unsafePath) Add(zip, "../escape.txt", "invalid");
            Add(zip, "manifest.json", new JsonObject { ["sessionId"] = source, ["appId"] = appId,
                ["startedAtUtc"] = "2026-10-11T01:00:00Z", ["stoppedAtUtc"] = "2026-10-11T01:01:00Z" }.ToJsonString());
            Add(zip, "records.jsonl", """
                {"channels":[{"id":1,"name":"FPS","unit":"fps"}]}
                {"type":"CLIENT_LOG","data":"Offline log","capturedAtUtc":"2026-10-11T01:00:01Z"}
                {"type":"CLIENT_TOUCH_INPUT","schema":"ansight.touches.v1","t0":"2026-10-11T01:00:01Z","space":"w","unit":"pt","surface":[100,100,1],"rows":[[0,0,1,20,30,0,1]]}
                {"type":"CLIENT_NETWORK_REQUEST","request":{"id":"request","source":"native","startedAtUtc":"2026-10-11T01:00:01Z","completedAtUtc":"2026-10-11T01:00:02Z","durationMilliseconds":1000,"method":"GET","url":"https://example.test/items","statusCode":200}}
                {"type":"CLIENT_METRICS","metrics":[{"value":60,"channel":1,"capturedAtUtc":"2026-10-11T01:00:01Z"}]}
                {"type":"CLIENT_VISUAL_TREE","snapshotId":"tree","capturedAtUtc":"2026-10-11T01:00:01Z","payload":{"type":"root"}}
                {"type":"CLIENT_EVENTS","events":[{"id":"event","label":"Tapped","eventType":"Event","channel":1,"capturedAtUtc":"2026-10-11T01:00:01Z"}]}
                """);
            Add(zip, "screenshots/index/frames.jsonl", """{"path":"screenshots/one.jpg","capturedAtUtc":"2026-10-11T01:00:01Z","width":1,"height":1,"quality":70}""");
            using var image = zip.CreateEntry("screenshots/one.jpg").Open();
            image.Write([0xff, 0xd8, 0xff, 0xd9]);
        }
        return memory.ToArray();
    }

    private static void Add(ZipArchive zip, string path, string content)
    {
        using var writer = new StreamWriter(zip.CreateEntry(path).Open());
        writer.Write(content);
    }
}
