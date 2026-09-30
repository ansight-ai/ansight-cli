namespace Ansight.Cli.Commands.App;

internal sealed class LocalAppGraphAgentRouteCatalog(
    LocalAppGraphService localAppGraphs,
    string appId,
    string appName) : IAppGraphAgentRouteCatalog
{
    private readonly LocalAppGraphService localAppGraphs = localAppGraphs
        ?? throw new ArgumentNullException(nameof(localAppGraphs));
    private readonly string appId = string.IsNullOrWhiteSpace(appId)
        ? throw new ArgumentException("An App ID is required.", nameof(appId))
        : appId.Trim();
    private readonly string appName = string.IsNullOrWhiteSpace(appName) ? appId.Trim() : appName.Trim();

    public Task<CloudRegisteredAppQueryResult> ListRegisteredAppsAsync(
        Guid teamId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(CloudRegisteredAppQueryResult.Success(
            [localAppGraphs.ResolveApp(appId, appName)]));
    }

    public Task<CloudAppGraphQueryResult> ListAppGraphsAsync(
        Guid teamId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(localAppGraphs.List(appId));
    }

    public Task<CloudAppGraphDetailResult> GetPublishedAppGraphAsync(
        Guid graphId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(localAppGraphs.Get(graphId, publishedOnly: false));
    }
}
