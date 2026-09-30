using Ansight.SimCtl;

namespace Ansight.RemoteSimulator.Core.Simulator.Apple;

public sealed record SimulatorTrackerSnapshot(
    DateTimeOffset CapturedAtUtc,
    IReadOnlyList<SimCtlDevice> Devices,
    string? Error)
{
    public static SimulatorTrackerSnapshot Empty { get; } =
        new(DateTimeOffset.MinValue, Array.Empty<SimCtlDevice>(), null);

    public IReadOnlyList<SimCtlDevice> BootedDevices => Devices.Where(static device => device.IsBooted).ToArray();
}
