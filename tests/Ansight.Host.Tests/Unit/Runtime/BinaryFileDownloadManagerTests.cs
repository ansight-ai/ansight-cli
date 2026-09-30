using System.Buffers.Binary;
using Ansight.Host.Tests.TestSupport;
using Ansight.Tools;

namespace Ansight.Host.Tests.Unit.Runtime;

public sealed class BinaryFileDownloadManagerTests
{
    [Fact]
    public void BeginDownload_CreatesExpectedToolCallEnvelope()
    {
        using var manager = new BinaryFileDownloadManager();

        var operation = manager.BeginDownload(new BinaryFileDownloadOptions
        {
            RequestId = "req_123",
            SessionId = "sess_1",
            SandboxRoot = "appData",
            SandboxPath = "db/app.db",
            DestinationDirectoryPath = Path.GetTempPath(),
            DownloadId = "download_123",
            ChunkBytes = 4096
        });

        Assert.Equal("req_123", operation.RequestId);
        Assert.Equal("sess_1", operation.SessionId);
        Assert.Equal(ToolProtocolMessageTypes.CallType, operation.RequestEnvelope.Type);
        Assert.Equal("req_123", operation.RequestEnvelope.Id);
        Assert.Equal("sess_1", operation.RequestEnvelope.SessionId);

        var payload = Assert.IsType<JsonObject>(operation.RequestEnvelope.Payload);
        Assert.Equal("files.begin_binary_download", payload["toolId"]?.GetValue<string>());
        var arguments = Assert.IsType<JsonObject>(payload["arguments"]);
        Assert.Equal("appData", arguments["root"]?.GetValue<string>());
        Assert.Equal("db/app.db", arguments["path"]?.GetValue<string>());
        Assert.Equal(4096, arguments["chunkBytes"]?.GetValue<int>());
        Assert.Equal("download_123", arguments["downloadId"]?.GetValue<string>());
    }

    [Fact]
    public async Task TryHandleToolEnvelope_AndBinaryFrames_CompletesLocalFile()
    {
        using var tempDirectory = TestDirectory.Create();
        using var manager = new BinaryFileDownloadManager();

        var operation = manager.BeginDownload(new BinaryFileDownloadOptions
        {
            RequestId = "req_1",
            SessionId = "sess_1",
            SandboxRoot = "workspace",
            SandboxPath = "payload.bin",
            DestinationDirectoryPath = tempDirectory.Path,
            DownloadId = "download_1"
        });

        var transferId = Guid.Parse("01234567-89ab-cdef-0123-456789abcdef");
        var metadataEnvelope = new ToolProtocolEnvelope
        {
            Type = ToolProtocolMessageTypes.ResultType,
            Id = "req_1.response",
            ReplyTo = "req_1",
            SessionId = "sess_1",
            Payload = new JsonObject
            {
                ["toolId"] = "files.begin_binary_download",
                ["success"] = true,
                ["result"] = new JsonObject
                {
                    ["downloadId"] = "download_1",
                    ["transferId"] = transferId.ToString("N"),
                    ["fileName"] = "payload.bin",
                    ["fileExtension"] = ".bin",
                    ["mimeType"] = "application/octet-stream",
                    ["sizeBytes"] = 5,
                    ["version"] = "5:123",
                    ["deliveryMode"] = "websocket_binary",
                    ["wireProtocol"] = BinaryFileTransferProtocol.ProtocolName
                }
            }
        };

        Assert.True(manager.TryHandleToolEnvelope(metadataEnvelope));

        var firstChunk = CreateFrame(
            transferId,
            BinaryFileTransferFrameType.Chunk,
            sequence: 0,
            offsetBytes: 0,
            payload: [1, 2, 3]);
        var secondChunk = CreateFrame(
            transferId,
            BinaryFileTransferFrameType.Chunk,
            sequence: 1,
            offsetBytes: 3,
            payload: [4, 5]);
        var complete = CreateFrame(
            transferId,
            BinaryFileTransferFrameType.Complete,
            sequence: 2,
            offsetBytes: 5,
            payload: []);

        Assert.True(manager.TryHandleBinaryMessage("sess_1", firstChunk));
        Assert.True(manager.TryHandleBinaryMessage("sess_1", secondChunk));
        Assert.True(manager.TryHandleBinaryMessage("sess_1", complete));

        var result = await operation.Completion.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal("download_1", result.DownloadId);
        Assert.Equal("payload.bin", result.RemoteFileName);
        Assert.Equal(".bin", result.RemoteFileExtension);
        Assert.Equal("application/octet-stream", result.MimeType);
        Assert.Equal(5, result.SizeBytes);
        Assert.StartsWith(tempDirectory.Path, result.LocalFilePath, StringComparison.Ordinal);
        Assert.Equal([1, 2, 3, 4, 5], await File.ReadAllBytesAsync(result.LocalFilePath));
    }

    [Fact]
    public async Task TryHandleToolEnvelope_ToolError_FailsOperation()
    {
        using var manager = new BinaryFileDownloadManager();

        var operation = manager.BeginDownload(new BinaryFileDownloadOptions
        {
            RequestId = "req_2",
            SessionId = "sess_2",
            SandboxPath = "missing.bin",
            DestinationDirectoryPath = Path.GetTempPath()
        });

        var envelope = new ToolProtocolEnvelope
        {
            Type = ToolProtocolMessageTypes.ErrorType,
            Id = "req_2.response",
            ReplyTo = "req_2",
            SessionId = "sess_2",
            Payload = new JsonObject
            {
                ["code"] = "filesystem_binary_download_unavailable",
                ["message"] = "Binary downloads require an active pairing WebSocket session."
            }
        };

        Assert.True(manager.TryHandleToolEnvelope(envelope));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => operation.Completion);
        Assert.Contains("filesystem_binary_download_unavailable", exception.Message, StringComparison.Ordinal);
    }

    private static byte[] CreateFrame(
        Guid transferId,
        BinaryFileTransferFrameType frameType,
        int sequence,
        long offsetBytes,
        byte[] payload)
    {
        var frame = new byte[BinaryFileTransferProtocol.HeaderSize + payload.Length];
        frame[0] = (byte)'A';
        frame[1] = (byte)'S';
        frame[2] = (byte)'F';
        frame[3] = (byte)'T';
        frame[4] = 1;
        frame[5] = (byte)frameType;
        frame[6] = 0;
        frame[7] = 0;
        Encoding.ASCII.GetBytes(transferId.ToString("N")).CopyTo(frame, 8);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(40, 4), sequence);
        BinaryPrimitives.WriteInt64LittleEndian(frame.AsSpan(44, 8), offsetBytes);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(52, 4), payload.Length);
        payload.CopyTo(frame, BinaryFileTransferProtocol.HeaderSize);
        return frame;
    }
}
