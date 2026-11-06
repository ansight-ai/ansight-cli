namespace Ansight.Host.Files;

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
