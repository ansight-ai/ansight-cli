namespace Ansight.Host.Devices.Location;

public sealed record SetDeviceLocationRequest(
    string? DeviceIdentifier,
    double Latitude,
    double Longitude);
