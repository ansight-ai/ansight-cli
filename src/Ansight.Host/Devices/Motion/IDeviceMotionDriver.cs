namespace Ansight.Host.Devices.Motion;

internal interface IDeviceMotionDriver
{
    Task ShakeIosSimulatorAsync(string deviceUdid, CancellationToken cancellationToken = default);

    Task PlayMotionAsync(
        string deviceSerial,
        IReadOnlyList<DeviceMotionSample> samples,
        CancellationToken cancellationToken = default);
}
