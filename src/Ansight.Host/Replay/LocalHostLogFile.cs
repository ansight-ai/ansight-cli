
namespace Ansight.Host.Replay;

public sealed record LocalHostLogFile(
    string FileName,
    long ByteCount,
    DateTimeOffset LastModifiedUtc);
