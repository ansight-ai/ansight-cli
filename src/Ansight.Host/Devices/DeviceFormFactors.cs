namespace Ansight.Host.Devices;

public static class DeviceFormFactors
{
    public const string Phone = "phone";
    public const string Tablet = "tablet";

    public static bool IsPhone(string? value)
        => string.Equals(value, Phone, StringComparison.OrdinalIgnoreCase);

    public static bool IsTablet(string? value)
        => string.Equals(value, Tablet, StringComparison.OrdinalIgnoreCase);
}
