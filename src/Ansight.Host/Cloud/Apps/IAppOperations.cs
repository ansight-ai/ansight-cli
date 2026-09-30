namespace Ansight.Host.Cloud.Apps;

public interface IAppOperations
{
    Task<CloudRegisteredAppQueryResult> ListRegisteredAppsAsync(
        Guid? teamId = null,
        CancellationToken cancellationToken = default);

    Task<CloudRegisteredAppOperationResult> RegisterAppAsync(
        Guid teamId,
        string appId,
        string name,
        string? platform = null,
        CancellationToken cancellationToken = default);

    Task<CloudRegisteredAppOperationResult> RemoveAppAsync(
        Guid teamId,
        string appId,
        CancellationToken cancellationToken = default);
}
