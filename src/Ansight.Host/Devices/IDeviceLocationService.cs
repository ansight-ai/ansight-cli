namespace Ansight.Host.Devices;

public interface IDeviceLocationService
{
    Task<DeviceOperationResult> SetLocationAsync(
        string platform,
        string deviceIdentifier,
        double latitude,
        double longitude,
        CancellationToken cancellationToken = default);

    Task<DeviceOperationResult> ClearLocationAsync(
        string platform,
        string deviceIdentifier,
        CancellationToken cancellationToken = default);
}
