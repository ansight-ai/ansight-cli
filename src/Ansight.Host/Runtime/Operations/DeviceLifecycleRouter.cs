using Ansight.Host;

namespace Ansight.Host.Runtime.Operations;

internal sealed class DeviceLifecycleRouter
{
    private const string UnavailableMessage =
        "This host has no device lifecycle driver. Start the resident host with ansight host run, then retry the device command.";
    private readonly Lock gate = new();
    private IDeviceLifecycleDriver? driver;

    public void Configure(IDeviceLifecycleDriver? value)
    {
        lock (gate)
        {
            driver = value;
        }
    }

    public Task<IReadOnlyList<DeviceLifecycleDevice>> ListDevicesAsync(
        CancellationToken cancellationToken)
        => GetDriver()?.ListDevicesAsync(cancellationToken)
           ?? Task.FromResult<IReadOnlyList<DeviceLifecycleDevice>>(
               Array.Empty<DeviceLifecycleDevice>());

    public Task<DeviceLifecycleResult> StartDeviceAsync(
        string deviceIdentifier,
        CancellationToken cancellationToken)
        => GetDriver()?.StartDeviceAsync(deviceIdentifier, cancellationToken)
           ?? Task.FromResult(DeviceLifecycleResult.Failure("startDevice", UnavailableMessage));

    public Task<DeviceLifecycleResult> LaunchApplicationAsync(
        string deviceIdentifier,
        string bundleIdentifier,
        CancellationToken cancellationToken)
        => GetDriver()?.LaunchApplicationAsync(deviceIdentifier, bundleIdentifier, cancellationToken)
           ?? Task.FromResult(DeviceLifecycleResult.Failure("launchApplication", UnavailableMessage));

    public async Task<DeviceLifecycleResult> ForegroundApplicationAsync(
        string deviceIdentifier,
        string bundleIdentifier,
        CancellationToken cancellationToken)
    {
        var result = await LaunchApplicationAsync(
            deviceIdentifier,
            bundleIdentifier,
            cancellationToken).ConfigureAwait(false);
        return result with { Operation = "foregroundApplication" };
    }

    public Task<DeviceLifecycleResult> BackgroundApplicationAsync(
        string deviceIdentifier,
        string bundleIdentifier,
        CancellationToken cancellationToken)
        => GetDriver()?.BackgroundApplicationAsync(deviceIdentifier, bundleIdentifier, cancellationToken)
           ?? Task.FromResult(DeviceLifecycleResult.Failure("backgroundApplication", UnavailableMessage));

    public Task<DeviceLifecycleResult> TerminateApplicationAsync(
        string deviceIdentifier,
        string bundleIdentifier,
        CancellationToken cancellationToken)
        => GetDriver()?.TerminateApplicationAsync(deviceIdentifier, bundleIdentifier, cancellationToken)
           ?? Task.FromResult(DeviceLifecycleResult.Failure("terminateApplication", UnavailableMessage));

    private IDeviceLifecycleDriver? GetDriver()
    {
        lock (gate)
        {
            return driver;
        }
    }
}
