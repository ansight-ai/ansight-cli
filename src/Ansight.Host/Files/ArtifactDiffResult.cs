namespace Ansight.Host.Files;

public sealed record ArtifactDiffResult(string Schema, IReadOnlyList<ArtifactListItem> Artifacts,
    IReadOnlyList<ArtifactDiffHop> Hops, bool IsComplete, string Summary);
