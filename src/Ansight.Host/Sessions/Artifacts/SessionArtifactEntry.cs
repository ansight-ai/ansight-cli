namespace Ansight.Host.Models.Session;

public sealed class SessionArtifactEntry
{
    public required string Name { get; init; }

    public required string RootAlias { get; init; }

    public required string RelativePath { get; init; }

    public required string SnapshotRelativePath { get; init; }

    public required string Kind { get; init; }

    public long SizeBytes { get; init; }

    public string FileExtension { get; init; } = string.Empty;

    public string MimeType { get; init; } = string.Empty;

    public string LastModifiedUtc { get; init; } = string.Empty;

    public string ArchiveRelativePath { get; init; } = string.Empty;
}
