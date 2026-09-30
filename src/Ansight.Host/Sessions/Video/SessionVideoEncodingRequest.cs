
namespace Ansight.Host;

public sealed record SessionVideoEncodingRequest(
    string OutputFilePath,
    int Width,
    int Height,
    int AverageBitRate,
    IReadOnlyList<SessionVideoEncodingFrame> Frames)
{
    public Action<int, int>? ReportProgress { get; init; }
}
