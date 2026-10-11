namespace Ansight.Host.Models.Session;

public sealed class SessionArtifactSnapshot
{
    public required string SnapshotId { get; init; }

    public required DateTimeOffset CapturedAtUtc { get; init; }

    public required string Source { get; init; }

    public required string RootAlias { get; init; }

    public string RootPath { get; init; } = string.Empty;

    public required string RelativePath { get; init; }

    public required string Name { get; init; }

    public required string Kind { get; init; }

    public required string ArtifactDirectoryName { get; init; }

    public int DirectoryCount { get; init; }

    public int FileCount { get; init; }

    public long ByteCount { get; init; }

    public bool Truncated { get; init; }

    public IReadOnlyList<SessionArtifactEntry> Entries { get; init; } = Array.Empty<SessionArtifactEntry>();
}
