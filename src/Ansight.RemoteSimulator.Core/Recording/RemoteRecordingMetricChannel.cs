namespace Ansight.RemoteSimulator.Core.Recording;

public sealed record RemoteRecordingMetricChannel(
    int Id,
    string Name,
    string ColorHex,
    string? Unit,
    string Type,
    string? Source,
    string? Group,
    string? Kind);
