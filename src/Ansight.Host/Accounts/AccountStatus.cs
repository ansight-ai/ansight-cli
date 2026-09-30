namespace Ansight.Host.Accounts;

public sealed record AccountStatus(
    string Schema,
    bool IsAuthenticated,
    string? UserId,
    string? Email,
    string? FullName,
    string? Company,
    string? AuthenticationMethod,
    DateTimeOffset? AuthenticatedAtUtc,
    DateTimeOffset? AccessTokenExpiresAtUtc,
    bool CanRefresh);

public sealed record AccountOperationResult(
    bool IsSuccess,
    string Message,
    AccountStatus Status);
