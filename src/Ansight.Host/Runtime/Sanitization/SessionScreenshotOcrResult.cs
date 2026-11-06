using System.Diagnostics;
using System.Globalization;

namespace Ansight.Host.Runtime.Sanitization;

internal sealed record SessionScreenshotOcrResult(
    bool Available,
    string? Provider,
    IReadOnlyList<SessionScreenshotTextBlock> Blocks,
    string? Message)
{
    public static SessionScreenshotOcrResult Unavailable(string message)
        => new(false, null, [], message);
}
