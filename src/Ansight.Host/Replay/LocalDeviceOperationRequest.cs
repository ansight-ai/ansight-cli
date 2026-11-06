
namespace Ansight.Host.Replay;

public sealed record LocalDeviceOperationRequest(
    string Platform,
    string DeviceIdentifier,
    string? ApplicationIdentifier = null,
    string? ApplicationPath = null);
