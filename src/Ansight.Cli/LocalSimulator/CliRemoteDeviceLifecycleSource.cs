using Ansight.Host;
using Ansight.RemoteSimulator.Core.Devices;
using Ansight.RemoteSimulator.Core.Runtime;

namespace Ansight.Cli.LocalSimulator;

internal sealed class CliRemoteDeviceLifecycleSource : IRemoteDeviceInventorySource
{
    private readonly IDeviceLifecycleDriver driver;

    public CliRemoteDeviceLifecycleSource(IDeviceLifecycleDriver driver)
    {
        this.driver = driver ?? throw new ArgumentNullException(nameof(driver));
    }

    public async Task<IReadOnlyList<RemoteBootableDevice>> ListBootableDevicesAsync(
        CancellationToken cancellationToken = default)
        => (await driver.ListDevicesAsync(cancellationToken).ConfigureAwait(false))
            .Where(static device => !device.IsBooted && DeviceKinds.IsVirtual(device.Kind))
            .Select(static device => new RemoteBootableDevice(
                device.Identifier,
                device.Name,
                device.Runtime,
                device.Platform))
            .ToArray();

    public async Task<IReadOnlyDictionary<string, IReadOnlyList<RemoteInstalledApplication>>> ListInstalledApplicationsAsync(
        CancellationToken cancellationToken = default)
        => (await driver.ListDevicesAsync(cancellationToken).ConfigureAwait(false))
            .Where(static device => device.IsBooted)
            .ToDictionary(
            static device => device.Identifier,
            static device => (IReadOnlyList<RemoteInstalledApplication>)device.InstalledApplications
                .Select(application => new RemoteInstalledApplication(
                    application.BundleIdentifier,
                    application.Name))
                .ToArray(),
            StringComparer.OrdinalIgnoreCase);

    public async Task<RemoteDeviceInventory> ListInventoryAsync(CancellationToken cancellationToken = default)
    {
        var devices = await driver.ListDevicesAsync(cancellationToken).ConfigureAwait(false);

        return new RemoteDeviceInventory(
            devices.Where(static device => !device.IsBooted && DeviceKinds.IsVirtual(device.Kind))
                .Select(static device => new RemoteBootableDevice(
                    device.Identifier,
                    device.Name,
                    device.Runtime,
                    device.Platform))
                .ToArray(),
            devices.Where(static device => device.IsBooted)
                .ToDictionary(
                static device => device.Identifier,
                static device => (IReadOnlyList<RemoteInstalledApplication>)device.InstalledApplications
                    .Select(application => new RemoteInstalledApplication(
                        application.BundleIdentifier,
                        application.Name))
                    .ToArray(),
                StringComparer.OrdinalIgnoreCase));
    }

    public async Task<RemoteOperationResult> StartDeviceAsync(
        string identifier,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(identifier))
        {
            return new RemoteOperationResult(false, "A simulator or emulator identifier is required.");
        }

        var result = await driver.StartDeviceAsync(identifier.Trim(), cancellationToken)
            .ConfigureAwait(false);

        return new RemoteOperationResult(result.IsSuccess, result.Message);
    }
}
