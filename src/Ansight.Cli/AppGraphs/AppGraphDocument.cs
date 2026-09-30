namespace Ansight.Cli.AppGraphs;

internal sealed class AppGraphDocument
{
    public string Schema { get; init; } = string.Empty;

    public IReadOnlyList<AppGraphDocumentNode> Nodes { get; init; } = [];

    public IReadOnlyList<AppGraphDocumentEdge> Edges { get; init; } = [];

    public IReadOnlyList<AppGraphDocumentNavigationHost> NavigationHosts { get; init; } = [];
}
