using System.Buffers.Binary;

namespace Ansight.Host.Tests.Unit.Runtime;

public sealed class SessionJpegWireProtocolTests
{
    [Fact]
    public void TryParse_ReturnsExpectedFrameForValidPayload()
    {
        var capturedAtUtc = DateTimeOffset.Parse("2026-03-20T00:00:00Z");
        var bytes = new byte[] { 1, 2, 3, 4, 5 };
        var payload = CreatePayload(capturedAtUtc, 320, 200, 75, bytes);

        var parsed = SessionJpegWireProtocol.TryParse(
            payload,
            out var parsedCapturedAtUtc,
            out var format,
            out var width,
            out var height,
            out var quality,
            out var parsedBytes);

        Assert.True(parsed);
        Assert.Equal(capturedAtUtc, parsedCapturedAtUtc);
        Assert.Equal("jpeg", format);
        Assert.Equal(320, width);
        Assert.Equal(200, height);
        Assert.Equal(75, quality);
        Assert.Equal(bytes, parsedBytes.ToArray());
    }

    [Fact]
    public void TryParse_ReturnsFalseForInvalidHeader()
    {
        var payload = CreatePayload(DateTimeOffset.UtcNow, 1, 1, 1, new byte[] { 1 });
        payload[0] = (byte)'X';

        var parsed = SessionJpegWireProtocol.TryParse(payload, out _, out _, out _, out _, out _, out _);

        Assert.False(parsed);
    }

    [Fact]
    public void TryParse_ReturnsFalseForMismatchedByteCount()
    {
        var payload = CreatePayload(DateTimeOffset.UtcNow, 1, 1, 1, new byte[] { 1, 2, 3 });
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(24, 4), 20);

        var parsed = SessionJpegWireProtocol.TryParse(payload, out _, out _, out _, out _, out _, out _);

        Assert.False(parsed);
    }

    private static byte[] CreatePayload(
        DateTimeOffset capturedAtUtc,
        int width,
        int height,
        int quality,
        byte[] bytes)
    {
        var payload = new byte[28 + bytes.Length];
        payload[0] = (byte)'A';
        payload[1] = (byte)'S';
        payload[2] = (byte)'J';
        payload[3] = (byte)'P';
        payload[4] = 1;
        payload[5] = 1;
        payload[6] = (byte)quality;
        BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(8, 8), capturedAtUtc.ToUnixTimeMilliseconds());
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(16, 4), width);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(20, 4), height);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(24, 4), bytes.Length);
        bytes.CopyTo(payload.AsSpan(28));
        return payload;
    }
}
