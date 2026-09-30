namespace Ansight.Host.Cloud;

public sealed record CloudSessionSummary(
    Guid Id,
    Guid TeamId,
    string AccessStatus,
    string? AppId,
    string? AppName,
    string? SourceSessionId,
    string Title,
    string StorageLayout,
    long StreamByteSize,
    DateTimeOffset UploadedAt,
    string? AuthorEmail,
    string? AuthorName)
{
    public string StorageBucket { get; init; } = "team-session-archives";

    public string StoragePath { get; init; } = string.Empty;

    public string? ManifestStoragePath { get; init; }

    public DateTimeOffset? ArchivedAt { get; init; }

    public string? RuntimePlatform { get; init; }
}
