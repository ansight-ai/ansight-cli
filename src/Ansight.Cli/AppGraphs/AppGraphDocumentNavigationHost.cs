namespace Ansight.Cli.AppGraphs;

internal sealed class AppGraphDocumentNavigationHost
{
    public string Id { get; init; } = string.Empty;

    public string DestinationId { get; init; } = string.Empty;

    public string ActiveChildDestinationId { get; init; } = string.Empty;

    public IReadOnlyList<string> ChildDestinationIds { get; init; } = [];
}
