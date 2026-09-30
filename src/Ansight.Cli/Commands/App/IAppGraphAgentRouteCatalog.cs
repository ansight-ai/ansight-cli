namespace Ansight.Cli.Commands.App;

internal interface IAppGraphAgentRouteCatalog
{
    Task<CloudRegisteredAppQueryResult> ListRegisteredAppsAsync(
        Guid teamId,
        CancellationToken cancellationToken);

    Task<CloudAppGraphQueryResult> ListAppGraphsAsync(
        Guid teamId,
        CancellationToken cancellationToken);

    Task<CloudAppGraphDetailResult> GetPublishedAppGraphAsync(
        Guid graphId,
        CancellationToken cancellationToken);
}
