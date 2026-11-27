namespace Ansight.RemoteSimulator.Core.Devices;

public sealed record RemoteBootableDevice(
    string Identifier,
    string Name,
    string Runtime,
    string Platform,
    DateTimeOffset? LastBootedUtc = null,
    DateTimeOffset? LastUsedUtc = null);
