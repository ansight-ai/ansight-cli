using Ansight.Host;

namespace Ansight.Cli.Commands.Device;

internal sealed record DeviceApplicationFilterResult(
    IReadOnlyList<DeviceDescriptor> Devices,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<DeviceApplicationInspectionFailure> Failures,
    IReadOnlyList<DeviceApplicationMatchOutput> Matches);
