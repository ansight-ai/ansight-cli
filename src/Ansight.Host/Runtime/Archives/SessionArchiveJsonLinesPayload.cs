namespace Ansight.Host.Runtime.Archives;

internal sealed class SessionArchiveJsonLinesPayload
{
    public required AppSessionSnapshot Snapshot { get; init; }
    public IReadOnlyDictionary<string, byte[]> ImageBytesByFrameId { get; init; } = new Dictionary<string, byte[]>(StringComparer.Ordinal);
    public byte[]? AppIconBytes { get; init; }
    public IReadOnlyDictionary<string, byte[]> ArtifactBytesByRelativePath { get; init; } = new Dictionary<string, byte[]>(StringComparer.Ordinal);
}
