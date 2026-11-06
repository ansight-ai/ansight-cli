using Ansight.Host;

namespace Ansight.Cli.Commands.Device;

internal sealed record DeviceApplicationMatchOutput(
    string DeviceIdentifier,
    InstalledApplication Application);
