namespace Ansight.Cli.Auth;

internal interface ICliAccountAccessAuthorizer : ICliAccessAuthorizer
{
    IDisposable BeginAccountScope(string expectedUserId);
}
