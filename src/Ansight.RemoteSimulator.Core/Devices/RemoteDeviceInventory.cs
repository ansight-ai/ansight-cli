namespace Ansight.RemoteSimulator.Core.Devices;

public sealed record RemoteDeviceInventory(
    IReadOnlyList<RemoteBootableDevice> BootableDevices,
    IReadOnlyDictionary<string, IReadOnlyList<RemoteInstalledApplication>> InstalledApplications);
