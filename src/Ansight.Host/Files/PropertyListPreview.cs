namespace Ansight.Host.Files;

internal sealed record PropertyListPreview(
    string SourceText,
    string FormatLabel,
    FileStructuredData StructuredData);
