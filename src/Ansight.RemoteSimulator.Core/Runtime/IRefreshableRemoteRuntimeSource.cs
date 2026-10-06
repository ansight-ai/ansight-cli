namespace Ansight.RemoteSimulator.Core.Runtime;

public interface IRefreshableRemoteRuntimeSource : IRemoteRuntimeSource
{
    Task<RemoteRuntimeSnapshot> RefreshIfStaleAsync(CancellationToken cancellationToken = default);

    Task<RemoteRuntimeSnapshot> RefreshAsync(CancellationToken cancellationToken = default);
}
