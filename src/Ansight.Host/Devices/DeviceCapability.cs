namespace Ansight.Host.Devices;

public sealed record DeviceCapability(
    string Platform,
    bool IsAvailable,
    string Backend,
    string Message,
    string? Status = null);
