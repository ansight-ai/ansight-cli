namespace Ansight.Host.Apps;

public sealed record AppWatchDeviceStatus(
    string Platform, string DeviceId, string DeviceName, string State, string? SessionId,
    string? LastSessionId, string? ProcessIdentity, string? Message);
