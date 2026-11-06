namespace Ansight.Host.Files;

public sealed record FileStructuredNode(
    string Name,
    string Kind,
    string? Value,
    IReadOnlyList<FileStructuredNode> Children);
