namespace Ansight.Host.Replay;

public sealed record SessionExplorerClearLocationRequest(
    string Platform,
    string DeviceIdentifier);
