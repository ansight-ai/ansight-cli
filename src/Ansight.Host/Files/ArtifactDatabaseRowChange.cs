namespace Ansight.Host.Files;

public sealed record ArtifactDatabaseRowChange(
    string Table, string Key, IReadOnlyList<string> ChangedColumns,
    System.Text.Json.Nodes.JsonObject? BeforeRow, System.Text.Json.Nodes.JsonObject? AfterRow,
    int BeforeCount, int AfterCount);
