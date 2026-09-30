using Ansight.Host;

namespace Ansight.Cli.Commands.Device;

internal sealed record DeviceApplicationsOutput(
    string Schema,
    string Platform,
    string DeviceIdentifier,
    IReadOnlyList<InstalledApplication> Applications);
