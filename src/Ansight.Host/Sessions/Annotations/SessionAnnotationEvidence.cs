namespace Ansight.Host.Models.Session;

public sealed class SessionAnnotationEvidence
{
    public required string Id { get; init; }

    public required string Kind { get; init; }

    public required string Status { get; init; }

    public string? Reason { get; init; }

    public DateTimeOffset? CapturedAtUtc { get; init; }

    public long? SizeBytes { get; init; }

    public bool Truncated { get; init; }
}
