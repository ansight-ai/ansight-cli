namespace Ansight.Host.Files;

public sealed record ArtifactDiffRequest(
    IReadOnlyList<ArtifactReference> Artifacts, string? Path = null, string Mode = "auto",
    string? ArrayKey = null, bool IgnoreWhitespace = false, int Offset = 0, int Limit = 500);
