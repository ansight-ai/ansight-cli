namespace Ansight.RemoteSimulator.Core.Companion;

public sealed record RemoteCompanionDevice(
    string Identifier,
    string Name,
    string Model,
    string Platform,
    string OperatingSystemVersion,
    string AppVersion);
