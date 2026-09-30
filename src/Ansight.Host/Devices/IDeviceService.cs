namespace Ansight.Host.Devices;

public interface IDeviceService
{
    Task<DeviceInventory> ListAsync(CancellationToken cancellationToken = default);

    Task<DeviceOperationResult> StartAsync(
        string platform,
        string deviceIdentifier,
        CancellationToken cancellationToken = default);

    Task<DeviceOperationResult> StartAsync(
        string platform,
        string deviceIdentifier,
        DeviceStartOptions options,
        CancellationToken cancellationToken = default);

    Task<DeviceOperationResult> ShowWindowAsync(
        string platform,
        string deviceIdentifier,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<InstalledApplication>> ListApplicationsAsync(
        string platform,
        string deviceIdentifier,
        CancellationToken cancellationToken = default);

    Task<string?> GetPhysicalIosProcessIdentityAsync(
        string deviceIdentifier,
        string applicationIdentifier,
        string bundlePath,
        CancellationToken cancellationToken = default);

    Task<ApplicationChecksum?> GetInstalledApplicationChecksumAsync(
        string platform,
        string deviceIdentifier,
        string applicationIdentifier,
        CancellationToken cancellationToken = default);

    Task<DeviceOperationResult> InstallApplicationAsync(
        string platform,
        string deviceIdentifier,
        string applicationPath,
        CancellationToken cancellationToken = default);

    Task<DeviceOperationResult> LaunchApplicationAsync(
        string platform,
        string deviceIdentifier,
        string applicationIdentifier,
        CancellationToken cancellationToken = default);

    Task<DeviceOperationResult> LaunchApplicationAsync(
        string platform,
        string deviceIdentifier,
        string applicationIdentifier,
        ApplicationLaunchOptions launchOptions,
        CancellationToken cancellationToken = default);

    Task<DeviceOperationResult> TerminateApplicationAsync(
        string platform,
        string deviceIdentifier,
        string applicationIdentifier,
        CancellationToken cancellationToken = default);
}
