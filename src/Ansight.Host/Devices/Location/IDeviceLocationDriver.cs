namespace Ansight.Host.Devices.Location;

public interface IDeviceLocationDriver
{
    Task<DeviceLocationResult> SetLocationAsync(
        SetDeviceLocationRequest request,
        CancellationToken cancellationToken = default);

    Task<DeviceLocationResult> ClearLocationAsync(
        ClearDeviceLocationRequest request,
        CancellationToken cancellationToken = default);
}
