namespace Ansight.RemoteSimulator.Core.Devices;

public sealed record RemoteBootableDevice(
    string Identifier,
    string Name,
    string Runtime,
    string Platform);
