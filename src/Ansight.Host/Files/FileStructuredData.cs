namespace Ansight.Host.Files;

public sealed record FileStructuredData(
    FileStructuredNode Root,
    bool IsTruncated);
