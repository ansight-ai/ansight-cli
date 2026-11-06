namespace Ansight.Cli.Auth;

internal interface ICliAccessAuthorizer
{
    Task<AccessDecision> CheckAsync(CancellationToken cancellationToken);
}
