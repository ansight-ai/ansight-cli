namespace Ansight.Adb;

public sealed record AdbLogcatRequest(
    string DeviceSerial,
    int ProcessId,
    IReadOnlyList<string>? Buffers = null);
