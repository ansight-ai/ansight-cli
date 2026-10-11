namespace Ansight.Host.Models.Session;

public sealed class SessionAppIcon
{
    public required string FileName { get; init; }
    public required string Format { get; init; }
    public string? MimeType { get; init; }
    public int? Width { get; init; }
    public int? Height { get; init; }
    public required long ByteCount { get; init; }
}
