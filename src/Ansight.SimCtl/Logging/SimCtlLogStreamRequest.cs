namespace Ansight.SimCtl;

public sealed record SimCtlLogStreamRequest(
    string DeviceUdid,
    int ProcessId,
    string Level = "debug");
