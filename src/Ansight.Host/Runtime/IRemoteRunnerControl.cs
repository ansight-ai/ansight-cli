namespace Ansight.Host.Runtime;

public sealed record RemoteRunnerRegistration(Guid MachineId, Guid TeamId, string DisplayName, IReadOnlyList<string> Kinds);

public sealed record RemoteRunnerControlResult(bool IsSuccess, string Message, RemoteRunnerRegistration? Registration = null);

public interface IRemoteRunnerControl : IAsyncDisposable
{
    ValueTask IAsyncDisposable.DisposeAsync() => ValueTask.CompletedTask;
    RemoteRunnerRegistration? GetRegistration();

    Task<RemoteRunnerControlResult> RegisterAsync(Guid teamId, string displayName, CancellationToken cancellationToken);

    Task<RemoteRunnerControlResult> StartAsync(CancellationToken cancellationToken);

    Task<RemoteRunnerControlResult> StopAsync(CancellationToken cancellationToken);
}
