using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Ansight.Host.Cloud;

public readonly record struct CloudSessionAttachmentResult(
    bool IsSuccess,
    string Message,
    CloudSessionAttachment? Attachment,
    string? FilePath)
{
    public static CloudSessionAttachmentResult Success(
        string message,
        CloudSessionAttachment attachment,
        string? filePath = null)
        => new(true, message, attachment, filePath);

    public static CloudSessionAttachmentResult Failure(string message)
        => new(false, message, null, null);
}
