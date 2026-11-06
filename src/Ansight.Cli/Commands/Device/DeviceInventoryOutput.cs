using Ansight.Host;

namespace Ansight.Cli.Commands.Device;

internal sealed record DeviceInventoryOutput(
    string Schema,
    IReadOnlyList<DeviceCapability> Capabilities,
    IReadOnlyList<DeviceDescriptor> Devices,
    IReadOnlyList<string> Warnings,
    string? ApplicationIdentifier,
    string? ApplicationVersion,
    string? ApplicationBuildVersion,
    IReadOnlyList<DeviceApplicationMatchOutput>? ApplicationMatches);
