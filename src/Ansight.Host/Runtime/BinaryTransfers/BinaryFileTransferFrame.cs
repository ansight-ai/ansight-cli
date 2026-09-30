namespace Ansight.Host.Runtime.BinaryTransfers;

public readonly record struct BinaryFileTransferFrame(
    BinaryFileTransferFrameHeader Header,
    ReadOnlyMemory<byte> Payload);
