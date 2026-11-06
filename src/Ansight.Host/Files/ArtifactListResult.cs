namespace Ansight.Host.Files;

public sealed record ArtifactListResult(string Schema, IReadOnlyList<ArtifactListItem> Items, int Total, int? NextOffset);
