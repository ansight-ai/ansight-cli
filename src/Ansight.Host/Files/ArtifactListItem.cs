namespace Ansight.Host.Files;

public sealed record ArtifactListItem(
    string SessionId, string SessionName, string ArtifactId, string Reference, string Name,
    string Provider, string LogicalId, DateTimeOffset CapturedAtUtc, int FileCount, long SizeBytes,
    bool Truncated, IReadOnlyList<string> Formats, IReadOnlyList<string> Paths);
