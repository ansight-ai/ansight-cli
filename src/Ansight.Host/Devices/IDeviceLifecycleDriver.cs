namespace Ansight.Host.Devices;

public interface IDeviceLifecycleDriver
{
    Task<IReadOnlyList<DeviceLifecycleDevice>> ListDevicesAsync(
        CancellationToken cancellationToken = default);

    Task<DeviceLifecycleResult> StartDeviceAsync(
        string deviceIdentifier,
        CancellationToken cancellationToken = default);

    Task<DeviceLifecycleResult> LaunchApplicationAsync(
        string deviceIdentifier,
        string bundleIdentifier,
        CancellationToken cancellationToken = default);

    Task<DeviceLifecycleResult> BackgroundApplicationAsync(
        string deviceIdentifier,
        string bundleIdentifier,
        CancellationToken cancellationToken = default);

    Task<DeviceLifecycleResult> TerminateApplicationAsync(
        string deviceIdentifier,
        string bundleIdentifier,
        CancellationToken cancellationToken = default);
}
