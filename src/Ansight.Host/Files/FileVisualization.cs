namespace Ansight.Host.Files;

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
