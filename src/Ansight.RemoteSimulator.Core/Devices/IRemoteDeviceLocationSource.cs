namespace Ansight.RemoteSimulator.Core.Devices;

public interface IRemoteDeviceLocationSource
{
    Task<RemoteDeviceLocationResult> SetLocationAsync(
        RemoteDeviceLocationRequest request,
        CancellationToken cancellationToken = default);

    Task<RemoteDeviceLocationResult> ClearLocationAsync(
        RemoteDeviceLocationClearRequest request,
        CancellationToken cancellationToken = default);
}
