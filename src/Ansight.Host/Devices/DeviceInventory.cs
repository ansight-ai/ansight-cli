namespace Ansight.Host.Devices;

public sealed record DeviceInventory(
    IReadOnlyList<DeviceCapability> Capabilities,
    IReadOnlyList<DeviceDescriptor> Devices,
    IReadOnlyList<string> Warnings);
