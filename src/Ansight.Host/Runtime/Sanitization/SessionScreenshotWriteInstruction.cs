using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SkiaSharp;

namespace Ansight.Host.Runtime.Sanitization;

internal sealed record SessionScreenshotWriteInstruction(
    SessionScreenshotWriteAction Action,
    IReadOnlyList<SessionSanitizationRegion> Regions)
{
    public static SessionScreenshotWriteInstruction Keep { get; } = new(SessionScreenshotWriteAction.Keep, []);

    public static SessionScreenshotWriteInstruction RedactAll { get; } = new(SessionScreenshotWriteAction.RedactAll, []);

    public static SessionScreenshotWriteInstruction Remove { get; } = new(SessionScreenshotWriteAction.Remove, []);
}
