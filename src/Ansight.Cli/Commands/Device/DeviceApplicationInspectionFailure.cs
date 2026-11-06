using Ansight.Host;

namespace Ansight.Cli.Commands.Device;

internal sealed record DeviceApplicationInspectionFailure(
    string DeviceName,
    string DeviceIdentifier,
    string Message);
