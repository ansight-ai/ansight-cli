using Ansight.Host;
using Ansight.RemoteSimulator.Core.Devices;
using Ansight.RemoteSimulator.Core.Runtime;

namespace Ansight.Cli.LocalSimulator;

internal sealed class CliRemoteDeviceLifecycleSource : IRemoteDeviceLifecycleSource
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

internal sealed class CliRemoteDeviceLocationSource : IRemoteDeviceLocationSource
{
    private readonly DeviceService devices;
    private readonly IRemoteRuntimeSource runtimeSource;

    public CliRemoteDeviceLocationSource(
        DeviceService devices,
        IRemoteRuntimeSource runtimeSource)
    {
        this.devices = devices ?? throw new ArgumentNullException(nameof(devices));
        this.runtimeSource = runtimeSource ?? throw new ArgumentNullException(nameof(runtimeSource));
    }

    public async Task<RemoteDeviceLocationResult> SetLocationAsync(
        RemoteDeviceLocationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var device = ResolveDevice(request.DeviceUdid);
        if (device is null)
        {
            return Failure(request.DeviceUdid, "The selected simulator or emulator is unavailable.");
        }

        var result = await devices.SetLocationAsync(
            device.Platform,
            device.Identifier,
            request.Latitude,
            request.Longitude,
            cancellationToken).ConfigureAwait(false);
        return ToRemoteResult(result, device);
    }

    public async Task<RemoteDeviceLocationResult> ClearLocationAsync(
        RemoteDeviceLocationClearRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var device = ResolveDevice(request.DeviceUdid);
        if (device is null)
        {
            return Failure(request.DeviceUdid, "The selected simulator or emulator is unavailable.");
        }

        var result = await devices.ClearLocationAsync(
            device.Platform,
            device.Identifier,
            cancellationToken).ConfigureAwait(false);
        return ToRemoteResult(result, device);
    }

    private RemoteRuntimeDevice? ResolveDevice(string identifier)
        => runtimeSource.Current.Devices.FirstOrDefault(device => string.Equals(
            device.Identifier,
            identifier,
            StringComparison.OrdinalIgnoreCase));

    private static RemoteDeviceLocationResult ToRemoteResult(
        DeviceOperationResult result,
        RemoteRuntimeDevice device)
        => new(
            result.IsSuccess,
            "ansight-cli-device-host",
            result.Message,
            device.Identifier,
            device.Name,
            device.Platform);

    private static RemoteDeviceLocationResult Failure(string identifier, string message)
        => new(false, "ansight-cli-device-host", message, identifier, null, null);
}
