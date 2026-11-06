using Ansight.Host;
using Ansight.RemoteSimulator.Core.Devices;
using Ansight.RemoteSimulator.Core.Runtime;

namespace Ansight.Cli.LocalSimulator;

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
