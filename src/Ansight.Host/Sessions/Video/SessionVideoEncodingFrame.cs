
namespace Ansight.Host;

public sealed record SessionVideoEncodingFrame(
    string? FrameId,
    string SourceFilePath,
    long PresentationTimeUs,
    long DurationUs,
    bool IsTerminalHold);
