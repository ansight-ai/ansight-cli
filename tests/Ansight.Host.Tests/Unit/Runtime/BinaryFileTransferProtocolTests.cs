using System.Buffers.Binary;

namespace Ansight.Host.Tests.Unit.Runtime;

public sealed class BinaryFileTransferProtocolTests
{
    [Fact]
    public void TryParseFrame_ReadsClientGeneratedAsftFrame()
    {
        var transferId = Guid.Parse("01234567-89ab-cdef-0123-456789abcdef");
        var payload = new byte[] { 1, 2, 3, 4, 5 };
        var frame = CreateFrame(
            transferId,
            BinaryFileTransferFrameType.Chunk,
            sequence: 4,
            offsetBytes: 128,
            payload);

        var success = BinaryFileTransferProtocol.TryParseFrame(frame, out var parsedFrame, out var error);

        Assert.True(success, error);
        Assert.Equal("0123456789abcdef0123456789abcdef", parsedFrame.Header.TransferId);
        Assert.Equal(BinaryFileTransferFrameType.Chunk, parsedFrame.Header.FrameType);
        Assert.Equal(4, parsedFrame.Header.Sequence);
        Assert.Equal(128L, parsedFrame.Header.OffsetBytes);
        Assert.Equal(payload.Length, parsedFrame.Header.PayloadByteCount);
        Assert.Equal(payload, parsedFrame.Payload.ToArray());
    }

    [Fact]
    public void TryParseFrame_RejectsNonAsftPayloads()
    {
        var success = BinaryFileTransferProtocol.TryParseFrame(new byte[] { 1, 2, 3 }, out _, out var error);

        Assert.False(success);
        Assert.Contains("not an ASFT", error, StringComparison.OrdinalIgnoreCase);
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
