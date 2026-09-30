namespace Ansight.Host.Runtime.WebSocketSessions;

using System.Buffers.Binary;

internal static class SessionJpegWireProtocol
{
    private const int HeaderSize = 28;
    private const byte Version = 1;
    private const byte FormatJpeg = 1;

    public static bool TryParse(
        ReadOnlyMemory<byte> payload,
        out DateTimeOffset capturedAtUtc,
        out string format,
        out int width,
        out int height,
        out int quality,
        out ReadOnlyMemory<byte> bytes)
    {
        capturedAtUtc = default;
        format = "jpeg";
        width = 0;
        height = 0;
        quality = 0;
        bytes = ReadOnlyMemory<byte>.Empty;

        var span = payload.Span;
        if (span.Length < HeaderSize
            || span[0] != (byte)'A'
            || span[1] != (byte)'S'
            || span[2] != (byte)'J'
            || span[3] != (byte)'P'
            || span[4] != Version
            || span[5] != FormatJpeg)
        {
            return false;
        }

        quality = span[6];
        capturedAtUtc = DateTimeOffset.FromUnixTimeMilliseconds(BinaryPrimitives.ReadInt64LittleEndian(span[8..16]));
        width = BinaryPrimitives.ReadInt32LittleEndian(span[16..20]);
        height = BinaryPrimitives.ReadInt32LittleEndian(span[20..24]);
        var byteCount = BinaryPrimitives.ReadInt32LittleEndian(span[24..28]);
        if (byteCount < 0 || span.Length != HeaderSize + byteCount)
        {
            return false;
        }

        bytes = payload[HeaderSize..];
        format = "jpeg";
        return true;
    }
}
