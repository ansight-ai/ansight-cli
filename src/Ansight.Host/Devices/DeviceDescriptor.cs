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

public static class DeviceFormFactors
{
    public const string Phone = "phone";
    public const string Tablet = "tablet";

    public static bool IsPhone(string? value)
        => string.Equals(value, Phone, StringComparison.OrdinalIgnoreCase);

    public static bool IsTablet(string? value)
        => string.Equals(value, Tablet, StringComparison.OrdinalIgnoreCase);
}
