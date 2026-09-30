namespace Ansight.Adb;

public sealed record AdbDevice(
    string Serial,
    string State,
    string? Product,
    string? Model,
    string? Device,
    string? TransportId)
{
    public bool IsConnected => string.Equals(State, "device", StringComparison.OrdinalIgnoreCase);
}
