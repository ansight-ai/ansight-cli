namespace Ansight.Host.Replay;

public sealed record SessionExplorerSetLocationRequest(
    string Platform,
    string DeviceIdentifier,
    double Latitude,
    double Longitude);
