namespace Ansight.RemoteSimulator.Core.Devices;

public sealed record RemoteInstalledApplication(
    string BundleIdentifier,
    string Name);
