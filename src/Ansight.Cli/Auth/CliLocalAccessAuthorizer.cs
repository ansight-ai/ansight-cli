namespace Ansight.Cli.Auth;

/// <summary>Local developer tools never consult an account or cloud entitlement.</summary>
internal sealed class CliLocalAccessAuthorizer : ICliAccessAuthorizer
{
    public static CliLocalAccessAuthorizer Instance { get; } = new();

    private CliLocalAccessAuthorizer() { }

    public Task<AccessDecision> CheckAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new AccessDecision(true, "local"));
    }
}
