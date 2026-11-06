namespace Ansight.Host.Accounts;

public sealed record AccountOperationResult(
    bool IsSuccess,
    string Message,
    AccountStatus Status);
