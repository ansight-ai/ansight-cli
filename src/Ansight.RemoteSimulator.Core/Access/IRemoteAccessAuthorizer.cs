namespace Ansight.RemoteSimulator.Core.Access;

public interface IRemoteAccessAuthorizer
{
    ValueTask<RemoteAccessAuthorization> AuthorizeAsync(
        string bearerToken,
        CancellationToken cancellationToken = default);
}
