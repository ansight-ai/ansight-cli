namespace Ansight.RemoteSimulator.Core.Devices;

public sealed record RemoteDeviceLocationResult(
    bool IsSuccess,
    string Backend,
    string Message,
    string? DeviceUdid,
    string? DeviceName,
    string? Platform);
