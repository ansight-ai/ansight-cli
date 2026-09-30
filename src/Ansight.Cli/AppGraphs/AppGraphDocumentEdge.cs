namespace Ansight.Cli.AppGraphs;

internal sealed class AppGraphDocumentEdge
{
    public string Id { get; init; } = string.Empty;

    public string From { get; init; } = string.Empty;

    public string To { get; init; } = string.Empty;

    public AppGraphDocumentAction? Action { get; init; }

    public IReadOnlyList<string> Postconditions { get; init; } = [];
}
