using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Ansight.Host.Cloud;

public readonly record struct CloudSessionAttachmentListResult(
    bool IsSuccess,
    string Message,
    IReadOnlyList<CloudSessionAttachment> Attachments)
{
    public static CloudSessionAttachmentListResult Success(IReadOnlyList<CloudSessionAttachment> attachments)
        => new(true, string.Empty, attachments);

    public static CloudSessionAttachmentListResult Failure(string message)
        => new(false, message, Array.Empty<CloudSessionAttachment>());
}
