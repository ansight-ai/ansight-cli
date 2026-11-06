namespace Ansight.Host.Files;

public sealed record ArtifactChange(string Path, string Kind, string? Before, string? After, ArtifactDatabaseRowChange? Database = null);
