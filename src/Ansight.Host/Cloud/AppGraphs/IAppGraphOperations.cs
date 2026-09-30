namespace Ansight.Host.Cloud.AppGraphs;

public interface IAppGraphOperations
{
    Task<CloudAppGraphQueryResult> ListAppGraphsAsync(
        Guid teamId,
        string? query = null,
        CancellationToken cancellationToken = default);

    Task<CloudAppGraphDetailResult> GetAppGraphAsync(
        Guid appGraphId,
        bool publishedOnly = false,
        CancellationToken cancellationToken = default);

    Task<CloudAppGraphCreateResult> CreateAppGraphAsync(
        CloudAppGraphCreateRequest request,
        CancellationToken cancellationToken = default);

    Task<CloudAppGraphObservationOperationResult> CreateAppGraphObservationAsync(
        CloudAppGraphObservationCreateRequest request,
        CancellationToken cancellationToken = default);

    Task<CloudAppGraphRunOperationResult> CreateAppGraphRunAsync(
        CloudAppGraphRunCreateRequest request,
        CancellationToken cancellationToken = default);

    Task<CloudAppGraphRunOperationResult> WriteAppGraphRunStepAsync(
        CloudAppGraphRunStepWriteRequest request,
        CancellationToken cancellationToken = default);

    Task<CloudAppGraphRunOperationResult> CompleteAppGraphRunAsync(
        Guid runId,
        string status,
        string? message,
        CancellationToken cancellationToken = default);
}
