namespace Ansight.Host.Devices;

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
