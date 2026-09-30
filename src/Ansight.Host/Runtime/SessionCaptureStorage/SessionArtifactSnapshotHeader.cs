namespace Ansight.Host.Runtime.SessionCaptureStorage;

using AppLifecycleState = global::Ansight.AppLifecycleState;
using Ansight.Host;

internal sealed class SessionArtifactSnapshotHeader
{
    public required string SnapshotId { get; init; }
    public required DateTimeOffset CapturedAtUtc { get; init; }
    public required string Source { get; init; }
    public required string RootAlias { get; init; }
    public required string RelativePath { get; init; }
    public required string Name { get; init; }
    public required string Kind { get; init; }
    public required string ArtifactDirectoryName { get; init; }
    public int DirectoryCount { get; init; }
    public int FileCount { get; init; }
    public long ByteCount { get; init; }
    public bool Truncated { get; init; }
}
