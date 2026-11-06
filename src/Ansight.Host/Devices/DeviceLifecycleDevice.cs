namespace Ansight.Host.Devices;

public sealed record DeviceLifecycleDevice(
    string Identifier,
    string Name,
    string Platform,
    string Runtime,
    string State,
    bool IsBooted,
    IReadOnlyList<DeviceLifecycleApplication> InstalledApplications,
    string Kind = DeviceKinds.Unknown);
