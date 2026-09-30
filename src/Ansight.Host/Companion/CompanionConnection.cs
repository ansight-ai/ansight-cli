namespace Ansight.Host.Companion;

public sealed record CompanionConnection(
    string SessionId,
    string DeviceIdentifier,
    string DeviceName,
    string Model,
    string Platform,
    string OperatingSystemVersion,
    string AppVersion,
    string TargetDeviceIdentifier,
    DateTimeOffset ConnectedAtUtc);
