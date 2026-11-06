using Ansight.Host.Runtime.Operations;
using Ansight.Infrastructure;

namespace Ansight.Host.Sessions;

public readonly record struct SessionScreenshotExportResult(
    bool IsSuccess,
    string Message,
    string? FilePath,
    SessionImageFrame? Frame)
{
    public static SessionScreenshotExportResult Success(string filePath, SessionImageFrame frame)
        => new(true, "Screenshot exported.", filePath, frame);

    public static SessionScreenshotExportResult Failure(string message)
        => new(false, message, null, null);
}
