namespace Ansight.Host.Devices;

internal sealed record CoreDeviceDescriptor(
    string Identifier,
    string Name,
    string Platform,
    string ProductType,
    string OperatingSystemVersion,
    string State,
    bool IsAvailable)
{
    public bool IsIos
        => Platform.Contains("ios", StringComparison.OrdinalIgnoreCase)
           || ProductType.StartsWith("iPhone", StringComparison.OrdinalIgnoreCase)
           || ProductType.StartsWith("iPad", StringComparison.OrdinalIgnoreCase)
           || ProductType.StartsWith("iPod", StringComparison.OrdinalIgnoreCase);
}
