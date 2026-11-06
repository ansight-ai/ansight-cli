namespace Ansight.Cli.Auth;

internal interface ICliLocalAccessProbe
{
    Task<AccessDecision?> CheckLocalAsync(string? expectedUserId, CancellationToken cancellationToken);
}
