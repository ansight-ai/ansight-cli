namespace Ansight.Host.Devices.Location;

public sealed record SetDeviceLocationRequest(
    string? DeviceIdentifier,
    double Latitude,
    double Longitude);

public sealed record ClearDeviceLocationRequest(
    string? DeviceIdentifier);

public sealed record DeviceLocationResult(
    bool IsSuccess,
    string Backend,
    string Message,
    string? DeviceIdentifier,
    string? DeviceName,
    string? Platform)
{
    public static DeviceLocationResult Failure(string message)
        => new(false, string.Empty, message, null, null, null);
}
