using Ansight.Host.Devices.Motion;

namespace Ansight.Host.Runtime.Operations;

internal sealed class DeviceMotionRouter
{
    private readonly Lock gate = new();
    private IDeviceMotionDriver? driver;

    public void Configure(IDeviceMotionDriver value)
    {
        lock (gate) driver = value;
    }

    public Task PlayAsync(
        string deviceSerial,
        IReadOnlyList<DeviceMotionSample> samples,
        CancellationToken cancellationToken)
    {
        IDeviceMotionDriver selected;
        lock (gate)
            selected = driver ?? throw new InvalidOperationException("This host has no device motion driver.");
        return selected.PlayMotionAsync(deviceSerial, samples, cancellationToken);
    }

    public Task ShakeIosSimulatorAsync(string deviceUdid, CancellationToken cancellationToken)
    {
        IDeviceMotionDriver selected;
        lock (gate)
            selected = driver ?? throw new InvalidOperationException("This host has no device motion driver.");
        return selected.ShakeIosSimulatorAsync(deviceUdid, cancellationToken);
    }
}
