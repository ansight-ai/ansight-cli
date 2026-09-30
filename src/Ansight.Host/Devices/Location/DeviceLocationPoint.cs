namespace Ansight.Host.Devices.Location;

public readonly record struct DeviceLocationPoint(
    double Latitude,
    double Longitude,
    double? AltitudeMeters = null,
    DateTimeOffset? Timestamp = null);
