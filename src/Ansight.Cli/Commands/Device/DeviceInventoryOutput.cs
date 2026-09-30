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

internal sealed record DeviceApplicationMatchOutput(
    string DeviceIdentifier,
    InstalledApplication Application);

internal sealed record DeviceApplicationFilterResult(
    IReadOnlyList<DeviceDescriptor> Devices,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<DeviceApplicationInspectionFailure> Failures,
    IReadOnlyList<DeviceApplicationMatchOutput> Matches);

internal sealed record DeviceApplicationInspectionFailure(
    string DeviceName,
    string DeviceIdentifier,
    string Message);
