using System.Buffers.Binary;
using System.Text;

namespace Ansight.Host.Runtime.BinaryTransfers;

public static class BinaryFileTransferProtocol
{
    public const string ProtocolName = "ansight.file-transfer.v1";
    public const int HeaderSize = 56;

    public static bool HasMagic(ReadOnlySpan<byte> message)
        => message.Length >= 4 &&
           message[0] == (byte)'A' &&
           message[1] == (byte)'S' &&
           message[2] == (byte)'F' &&
           message[3] == (byte)'T';

    public static bool TryParseFrame(ReadOnlyMemory<byte> message, out BinaryFileTransferFrame frame, out string error)
    {
        frame = default;
        error = string.Empty;

        if (!HasMagic(message.Span))
        {
            error = "The payload is not an ASFT binary transfer frame.";
            return false;
        }

        if (message.Length < HeaderSize)
        {
            error = $"The ASFT frame was shorter than the required {HeaderSize} byte header.";
            return false;
        }

        var span = message.Span;
        var version = span[4];
        if (version != 1)
        {
            error = $"Unsupported ASFT protocol version '{version}'.";
            return false;
        }

        var frameTypeValue = span[5];
        if (!Enum.IsDefined(typeof(BinaryFileTransferFrameType), frameTypeValue))
        {
            error = $"Unsupported ASFT frame type '{frameTypeValue}'.";
            return false;
        }

        var transferId = Encoding.ASCII.GetString(span[8..40]);
        if (transferId.Length != 32 || !Guid.TryParseExact(transferId, "N", out _))
        {
            error = "The ASFT frame contains an invalid transfer id.";
            return false;
        }

        var payloadByteCount = BinaryPrimitives.ReadInt32LittleEndian(span[52..56]);
        if (payloadByteCount < 0)
        {
            error = "The ASFT frame contains a negative payload length.";
            return false;
        }

        if (message.Length != HeaderSize + payloadByteCount)
        {
            error = $"The ASFT frame payload length '{payloadByteCount}' did not match the received byte count.";
            return false;
        }

        frame = new BinaryFileTransferFrame(
            new BinaryFileTransferFrameHeader(
                transferId,
                (BinaryFileTransferFrameType)frameTypeValue,
                BinaryPrimitives.ReadInt32LittleEndian(span[40..44]),
                BinaryPrimitives.ReadInt64LittleEndian(span[44..52]),
                payloadByteCount),
            message[HeaderSize..]);

        return true;
    }
}
