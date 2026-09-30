namespace Ansight.Host.Tests.TestSupport;

internal sealed class FakeHostDeviceLocationService : IDeviceLocationService
{
    public ConcurrentQueue<DeviceLocationPoint> Locations { get; } = new();

    public Task<DeviceOperationResult> SetLocationAsync(
        string platform,
        string deviceIdentifier,
        double latitude,
        double longitude,
        CancellationToken cancellationToken = default)
    {
        Locations.Enqueue(new DeviceLocationPoint(latitude, longitude, null, null));
        return Task.FromResult(DeviceOperationResult.Success(
            "set-location",
            platform,
            deviceIdentifier,
            "Location set."));
    }

    public Task<DeviceOperationResult> ClearLocationAsync(
        string platform,
        string deviceIdentifier,
        CancellationToken cancellationToken = default)
        => Task.FromResult(DeviceOperationResult.Success(
            "clear-location",
            platform,
            deviceIdentifier,
            "Location cleared."));
}
