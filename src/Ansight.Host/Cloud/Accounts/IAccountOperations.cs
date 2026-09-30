namespace Ansight.Host.Cloud.Accounts;

public interface IAccountOperations
{
    Task<CloudAccountGrantQueryResult> ListAccountGrantsAsync(
        Guid? teamId = null,
        CancellationToken cancellationToken = default);

    Task<CloudAccountUsageResult> GetAccountUsageAsync(
        Guid teamId,
        CancellationToken cancellationToken = default);
}
