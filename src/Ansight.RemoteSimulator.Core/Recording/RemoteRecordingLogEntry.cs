namespace Ansight.RemoteSimulator.Core.Recording;

public sealed record RemoteRecordingLogEntry(
    DateTimeOffset TimestampUtc,
    string StreamId,
    string Priority,
    string Source,
    string Tag,
    string Message,
    string? EventId);
