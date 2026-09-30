namespace Ansight.Host.Files;

public static class FileViewerKinds
{
    public const string Binary = "binary";
    public const string Code = "code";
    public const string Csv = "csv";
    public const string Image = "image";
    public const string Json = "json";
    public const string Markdown = "markdown";
    public const string Model3D = "model3d";
    public const string Pdf = "pdf";
    public const string Sqlite = "sqlite";
    public const string Text = "text";
    public const string Video = "video";
    public const string Audio = "audio";
    public const string Xml = "xml";
}

public sealed record FileVisualization(
    bool IsSuccess,
    string Message,
    string ViewerKind,
    string FileName,
    string FileExtension,
    string MimeType,
    string Language,
    long SizeBytes,
    long BytesRead,
    bool IsTruncated,
    string? Text,
    string? Base64,
    FileDatabaseSchema? DatabaseSchema,
    string? RawText = null,
    string? FormatLabel = null,
    FileStructuredData? StructuredData = null,
    FileArtifactDetails? ArtifactDetails = null)
{
    public static FileVisualization Failure(string message)
        => new(
            false,
            message,
            FileViewerKinds.Binary,
            string.Empty,
            string.Empty,
            "application/octet-stream",
            "Binary",
            0,
            0,
            false,
            null,
            null,
            null);
}

public sealed record FileArtifactDetails(
    string DownloadedAtUtc,
    string Sha256,
    string Sha1,
    string Md5);

public sealed record FileContentSource(
    bool IsSuccess,
    string Message,
    string? FilePath,
    string FileName,
    string MimeType,
    long SizeBytes)
{
    public static FileContentSource Failure(string message)
        => new(false, message, null, string.Empty, "application/octet-stream", 0);
}

public sealed record FileStructuredData(
    FileStructuredNode Root,
    bool IsTruncated);

public sealed record FileStructuredNode(
    string Name,
    string Kind,
    string? Value,
    IReadOnlyList<FileStructuredNode> Children);

public sealed record FileDatabaseSchema(
    string DatabasePath,
    IReadOnlyList<FileDatabaseObject> Objects);

public sealed record FileDatabaseObject(
    string Name,
    string Type,
    string Sql,
    IReadOnlyList<FileDatabaseColumn> Columns);

public sealed record FileDatabaseColumn(
    string Name,
    string Key,
    string DeclaredType,
    bool IsNullable,
    bool IsPrimaryKey,
    string DefaultValue);

public sealed record FileDatabaseQueryResult(
    bool IsSuccess,
    string Message,
    string Sql,
    IReadOnlyList<FileDatabaseColumn> Columns,
    IReadOnlyList<IReadOnlyList<string>> Rows,
    bool IsTruncated)
{
    public static FileDatabaseQueryResult Failure(string message)
        => new(false, message, string.Empty, [], [], false);
}
