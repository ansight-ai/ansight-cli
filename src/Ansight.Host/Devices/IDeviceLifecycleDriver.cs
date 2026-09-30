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

public sealed record DeviceLifecycleApplication(
    string BundleIdentifier,
    string Name);

public sealed record DeviceLifecycleDevice(
    string Identifier,
    string Name,
    string Platform,
    string Runtime,
    string State,
    bool IsBooted,
    IReadOnlyList<DeviceLifecycleApplication> InstalledApplications,
    string Kind = DeviceKinds.Unknown);

public sealed record DeviceLifecycleResult(
    bool IsSuccess,
    string DeviceIdentifier,
    string Platform,
    string Operation,
    string? BundleIdentifier,
    string Message)
{
    public static DeviceLifecycleResult Failure(
        string operation,
        string message,
        string deviceIdentifier = "",
        string platform = "",
        string? bundleIdentifier = null)
        => new(false, deviceIdentifier, platform, operation, bundleIdentifier, message);
}
