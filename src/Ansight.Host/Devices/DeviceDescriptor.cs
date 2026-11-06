namespace Ansight.Host.Devices;

public sealed record DeviceDescriptor(
    string Identifier,
    string Name,
    string Platform,
    string Runtime,
    string State,
    bool IsBooted,
    bool IsAvailable,
    string Kind)
{
    public bool IsPhysical => DeviceKinds.IsPhysical(Kind);

    public bool IsVirtual => DeviceKinds.IsVirtual(Kind);

    public string? FormFactor { get; init; }
}
