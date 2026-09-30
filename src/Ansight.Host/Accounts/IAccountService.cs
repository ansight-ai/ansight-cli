
namespace Ansight.Host.Accounts;
public interface IAccountService
{
    AccountStatus GetStatus();
    Task<AccountOperationResult> RequestEmailCodeAsync(string email, CancellationToken cancellationToken = default);
    Task<AccountOperationResult> SignInWithPasswordAsync(string email, string password, CancellationToken cancellationToken = default);
    Task<AccountOperationResult> SignInWithEmailCodeAsync(string email, string code, CancellationToken cancellationToken = default);
    Task<AccountOperationResult> RefreshAsync(CancellationToken cancellationToken = default);
    Task<AccountOperationResult> SignOutAsync(bool revokeRemoteSession = true, CancellationToken cancellationToken = default);
}
