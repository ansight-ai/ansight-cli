using Ansight.RemoteSimulator.Core.Simulator.Android;

namespace Ansight.RemoteSimulator.Core.Simulator.Android.Grpc;

internal static class MessageCodec
{
    public static byte[] EncodeImageFormat(int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Image dimensions must be positive.");
        }

        using var output = new MemoryStream();
        WriteVarintField(output, 3, (ulong)width);
        WriteVarintField(output, 4, (ulong)height);
        return output.ToArray();
    }

    public static byte[] EncodeTouchEvent(IReadOnlyList<AndroidEmulatorTouch> touches)
    {
        ArgumentNullException.ThrowIfNull(touches);
        using var output = new MemoryStream();
        foreach (var touch in touches)
        {
            using var encodedTouch = new MemoryStream();
            WriteVarintField(encodedTouch, 1, (ulong)Math.Max(0, touch.X));
            WriteVarintField(encodedTouch, 2, (ulong)Math.Max(0, touch.Y));
            WriteVarintField(encodedTouch, 3, (ulong)Math.Max(0, touch.Identifier));
            WriteVarintField(encodedTouch, 4, (ulong)Math.Max(0, touch.Pressure));
            WriteVarintField(encodedTouch, 5, 8);
            WriteLengthDelimitedField(output, 1, encodedTouch.ToArray());
        }

        return output.ToArray();
    }

    public static byte[] DecodeImagePng(ReadOnlySpan<byte> message)
    {
        var offset = 0;
        while (offset < message.Length)
        {
            var tag = ReadVarint(message, ref offset);
            var fieldNumber = (int)(tag >> 3);
            var wireType = (int)(tag & 7);
            if (fieldNumber == 4 && wireType == 2)
            {
                var length = checked((int)ReadVarint(message, ref offset));
                if (length < 0 || offset + length > message.Length)
                {
                    throw new InvalidOperationException("Android Emulator gRPC returned a truncated PNG payload.");
                }

                return message.Slice(offset, length).ToArray();
            }

            SkipField(message, ref offset, wireType);
        }

        return [];
    }

    private static void WriteVarintField(Stream output, int fieldNumber, ulong value)
    {
        WriteVarint(output, (ulong)(fieldNumber << 3));
        WriteVarint(output, value);
    }

    private static void WriteLengthDelimitedField(Stream output, int fieldNumber, byte[] value)
    {
        WriteVarint(output, (ulong)((fieldNumber << 3) | 2));
        WriteVarint(output, (ulong)value.Length);
        output.Write(value);
    }

    private static void WriteVarint(Stream output, ulong value)
    {
        while (value >= 0x80)
        {
            output.WriteByte((byte)((value & 0x7F) | 0x80));
            value >>= 7;
        }
        output.WriteByte((byte)value);
    }

    private static ulong ReadVarint(ReadOnlySpan<byte> message, ref int offset)
    {
        ulong value = 0;
        for (var shift = 0; shift < 64; shift += 7)
        {
            if (offset >= message.Length)
            {
                throw new InvalidOperationException("Android Emulator gRPC returned a truncated protobuf value.");
            }

            var current = message[offset++];
            value |= (ulong)(current & 0x7F) << shift;
            if ((current & 0x80) == 0)
            {
                return value;
            }
        }

        throw new InvalidOperationException("Android Emulator gRPC returned an invalid protobuf value.");
    }

    private static void SkipField(ReadOnlySpan<byte> message, ref int offset, int wireType)
    {
        switch (wireType)
        {
            case 0:
                _ = ReadVarint(message, ref offset);
                break;
            case 1:
                offset = checked(offset + 8);
                break;
            case 2:
                var length = checked((int)ReadVarint(message, ref offset));
                offset = checked(offset + length);
                break;
            case 5:
                offset = checked(offset + 4);
                break;
            default:
                throw new InvalidOperationException(
                    $"Android Emulator gRPC used unsupported protobuf wire type {wireType}.");
        }

        if (offset > message.Length)
        {
            throw new InvalidOperationException("Android Emulator gRPC returned a truncated protobuf field.");
        }
    }
}
