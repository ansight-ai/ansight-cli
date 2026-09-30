namespace Ansight.Host.Runtime.BinaryTransfers;

public enum BinaryFileTransferFrameType : byte
{
    Chunk = 1,
    Complete = 2,
    Error = 3
}
