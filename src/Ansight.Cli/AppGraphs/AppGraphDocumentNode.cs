namespace Ansight.Cli.AppGraphs;

internal sealed class AppGraphDocumentNode
{
    public string Id { get; init; } = string.Empty;

    public string Kind { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public IReadOnlyList<string>? Synonyms { get; init; }

    public string Purpose { get; init; } = string.Empty;

    public string? ParentScreen { get; init; }

    public bool IsEntry { get; init; }
}
