namespace Ansight.Host.Files;

public sealed record ArtifactReference(string SessionId, string ArtifactId)
{
    public string Reference => $"{SessionId}/{ArtifactId}";

    public static ArtifactReference Parse(string value)
    {
        var parts = value.Split('/');
        if (parts.Length != 2 || parts.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("An artifact reference must be <session-id>/<artifact-id>.");
        return new(parts[0], parts[1]);
    }
}

public sealed record ArtifactListRequest(
    string SessionId, string? Search = null, string? Provider = null, string? Format = null,
    DateTimeOffset? From = null, DateTimeOffset? To = null, long? MinBytes = null, long? MaxBytes = null,
    int Offset = 0, int Limit = 200, string Sort = "captured-at");

public sealed record ArtifactListItem(
    string SessionId, string SessionName, string ArtifactId, string Reference, string Name,
    string Provider, string LogicalId, DateTimeOffset CapturedAtUtc, int FileCount, long SizeBytes,
    bool Truncated, IReadOnlyList<string> Formats, IReadOnlyList<string> Paths);

public sealed record ArtifactListResult(string Schema, IReadOnlyList<ArtifactListItem> Items, int Total, int? NextOffset);

public sealed record ArtifactDiffRequest(
    IReadOnlyList<ArtifactReference> Artifacts, string? Path = null, string Mode = "auto",
    string? ArrayKey = null, bool IgnoreWhitespace = false, int Offset = 0, int Limit = 500);

public sealed record ArtifactChange(string Path, string Kind, string? Before, string? After, ArtifactDatabaseRowChange? Database = null);
public sealed record ArtifactFileDiff(
    string Path, string Format, string Status, bool ByteIdentical, bool IsComplete,
    string? Message, int TotalChanges, IReadOnlyList<ArtifactChange> Changes, int? NextOffset);
public sealed record ArtifactDiffHop(
    int Index, ArtifactListItem Before, ArtifactListItem After, string Status, bool IsComplete,
    IReadOnlyList<ArtifactFileDiff> Files);
public sealed record ArtifactDiffResult(string Schema, IReadOnlyList<ArtifactListItem> Artifacts,
    IReadOnlyList<ArtifactDiffHop> Hops, bool IsComplete, string Summary);

public sealed record ArtifactDatabaseRowChange(
    string Table, string Key, IReadOnlyList<string> ChangedColumns,
    System.Text.Json.Nodes.JsonObject? BeforeRow, System.Text.Json.Nodes.JsonObject? AfterRow,
    int BeforeCount, int AfterCount);
