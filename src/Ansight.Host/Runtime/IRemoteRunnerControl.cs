namespace Ansight.Host.Runtime;

public interface IRemoteRunnerControl : IAsyncDisposable
{
    ValueTask IAsyncDisposable.DisposeAsync() => ValueTask.CompletedTask;

    RemoteRunnerRegistration? GetRegistration();

    Task<RemoteRunnerControlResult> RegisterAsync(Guid teamId, string displayName, CancellationToken cancellationToken);

    Task<RemoteRunnerControlResult> StartAsync(CancellationToken cancellationToken);

    Task<RemoteRunnerControlResult> StopAsync(CancellationToken cancellationToken);
}
