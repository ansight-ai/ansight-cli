using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Ansight.Host.Cloud;

public readonly record struct CloudSessionDownloadResult(
    bool IsSuccess,
    string Message,
    string? FilePath)
{
    public static CloudSessionDownloadResult Success(string filePath)
        => new(true, "Cloud session downloaded.", filePath);

    public static CloudSessionDownloadResult Failure(string message)
        => new(false, message, null);
}
