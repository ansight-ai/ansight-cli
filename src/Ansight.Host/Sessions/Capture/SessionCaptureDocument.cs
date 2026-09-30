namespace Ansight.Host.Sessions.Capture;

internal sealed class SessionCaptureDocument
{
    public const string LegacySchemaName = "ansight.session-capture.v1";
    public const string SchemaName = "ansight.session-capture.v2";

    public string Schema { get; init; } = SchemaName;
    public required DateTimeOffset SavedAtUtc { get; init; }
    public SessionCaptureAuthorMetadata? Author { get; init; }
    public required AppSessionSnapshot Session { get; init; }
    public IReadOnlyList<SessionVisualTreeSnapshotIndexEntry> VisualTreeSnapshotIndex { get; init; } = [];

    public static bool IsSupportedSchema(string? schema)
        => string.Equals(schema, LegacySchemaName, StringComparison.Ordinal)
           || string.Equals(schema, SchemaName, StringComparison.Ordinal);
}

internal sealed class SessionVisualTreeSnapshotIndexEntry
{
    public required string EntryPath { get; init; }
    public required SessionVisualTreeSnapshotHeader Header { get; init; }
}
