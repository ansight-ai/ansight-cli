namespace Ansight.Host.Files;

public sealed record ArtifactDiffHop(
    int Index, ArtifactListItem Before, ArtifactListItem After, string Status, bool IsComplete,
    IReadOnlyList<ArtifactFileDiff> Files);
