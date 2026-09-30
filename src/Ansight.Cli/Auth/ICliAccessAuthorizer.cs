namespace Ansight.Cli.Auth;

internal interface ICliAccessAuthorizer
{
    Task<AccessDecision> CheckAsync(CancellationToken cancellationToken);
}

internal interface ICliLocalAccessProbe
{
    Task<AccessDecision?> CheckLocalAsync(string? expectedUserId, CancellationToken cancellationToken);
}
