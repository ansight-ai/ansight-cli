namespace Ansight.RemoteSimulator.Core.Devices;

public interface IRemoteDeviceLifecycleSource
{
    Task<IReadOnlyList<RemoteBootableDevice>> ListBootableDevicesAsync(
        CancellationToken cancellationToken = default);

    Task<IReadOnlyDictionary<string, IReadOnlyList<RemoteInstalledApplication>>> ListInstalledApplicationsAsync(
        CancellationToken cancellationToken = default);

    Task<RemoteOperationResult> StartDeviceAsync(
        string identifier,
        CancellationToken cancellationToken = default);
}
