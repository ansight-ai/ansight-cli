namespace Ansight.Host.Models.Session;

public sealed class SessionNetworkBody
{
    public string? ContentType { get; init; }
    public required string Encoding { get; init; }
    public required string Data { get; init; }
    public required long CapturedBytes { get; init; }
    public long? TotalBytes { get; init; }
    public required bool Truncated { get; init; }
}
