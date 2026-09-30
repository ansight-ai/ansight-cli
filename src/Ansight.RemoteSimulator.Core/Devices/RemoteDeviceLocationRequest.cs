namespace Ansight.RemoteSimulator.Core.Devices;

public sealed record RemoteDeviceLocationRequest(
    string DeviceUdid,
    double Latitude,
    double Longitude);
