namespace Ansight.Host.Cloud.Runners;

public interface IRunnerOperations
{
    Task<CloudRunnerKeyQueryResult> ListRunnerKeysAsync(
        Guid teamId,
        CancellationToken cancellationToken = default);

    Task<CloudRunnerKeyIssueResult> IssueRunnerKeyAsync(
        CloudRunnerKeyIssueRequest request,
        CancellationToken cancellationToken = default);

    Task<CloudRunnerKeyOperationResult> RevokeRunnerKeyAsync(
        Guid keyId,
        CancellationToken cancellationToken = default);
}
