namespace Ansight.Host.Files;

public sealed record ArtifactFileDiff(
    string Path, string Format, string Status, bool ByteIdentical, bool IsComplete,
    string? Message, int TotalChanges, IReadOnlyList<ArtifactChange> Changes, int? NextOffset);
