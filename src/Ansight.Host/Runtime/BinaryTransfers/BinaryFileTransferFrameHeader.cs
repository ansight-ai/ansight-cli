namespace Ansight.Host.Runtime.BinaryTransfers;

public readonly record struct BinaryFileTransferFrameHeader(
    string TransferId,
    BinaryFileTransferFrameType FrameType,
    int Sequence,
    long OffsetBytes,
    int PayloadByteCount);
