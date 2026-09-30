namespace Ansight.Cli.Auth;

internal sealed record CliAccessContext(ICliAccessAuthorizer Authorizer, CliAccessLease Lease)
{
    private static readonly AsyncLocal<CliAccessContext?> current = new();
    public static CliAccessContext? Current => current.Value;

    public static IDisposable Push(ICliAccessAuthorizer authorizer, CliAccessLease lease)
    {
        var previous = current.Value;
        current.Value = new CliAccessContext(authorizer, lease);
        return new CliCommandContextScope(() => current.Value = previous);
    }
}
