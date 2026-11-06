using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Ansight.Host.Cloud;

public sealed record CloudSessionAttachment(
    Guid Id,
    Guid SessionId,
    Guid TeamId,
    string UploadedByUserId,
    string Name,
    string Notes,
    string StorageBucket,
    string StoragePath,
    long ByteSize,
    string ContentType,
    string OriginalFileName,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
