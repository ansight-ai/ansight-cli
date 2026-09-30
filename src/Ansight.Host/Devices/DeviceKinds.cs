namespace Ansight.Host.Devices;

public static class DeviceKinds
{
    public const string Physical = "physical";

    public const string Virtual = "virtual";

    public const string Device = "device";

    public const string Emulator = "emulator";

    public const string Avd = "avd";

    public const string Simulator = "simulator";

    public const string Unknown = "unknown";

    public static bool IsPhysical(string? kind)
        => string.Equals(kind, Device, StringComparison.OrdinalIgnoreCase)
           || string.Equals(kind, Physical, StringComparison.OrdinalIgnoreCase);

    public static bool IsVirtual(string? kind)
        => string.Equals(kind, Emulator, StringComparison.OrdinalIgnoreCase)
           || string.Equals(kind, Avd, StringComparison.OrdinalIgnoreCase)
           || string.Equals(kind, Simulator, StringComparison.OrdinalIgnoreCase)
           || string.Equals(kind, Virtual, StringComparison.OrdinalIgnoreCase);

    public static bool Matches(string? requestedKind, string? actualKind)
        => string.IsNullOrWhiteSpace(requestedKind)
           || (string.Equals(requestedKind, Physical, StringComparison.OrdinalIgnoreCase)
               ? IsPhysical(actualKind)
               : string.Equals(requestedKind, Virtual, StringComparison.OrdinalIgnoreCase)
                 ? IsVirtual(actualKind)
                 : string.Equals(requestedKind, actualKind, StringComparison.OrdinalIgnoreCase));
}
