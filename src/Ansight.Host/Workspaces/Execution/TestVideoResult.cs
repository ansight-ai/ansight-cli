namespace Ansight.Host.Workspaces.Execution;

public sealed record TestVideoResult(
    string RunId,
    string SessionId,
    string Status,
    string Message,
    string Source,
    string? DeviceIdentifier,
    DateTimeOffset StartedUtc,
    DateTimeOffset CompletedUtc,
    string? RecordingId = null,
    string? Path = null)
{
    public long? ByteSize { get; init; }
    public long? DurationUs { get; init; }
    public int? Width { get; init; }
    public int? Height { get; init; }
    // Host observations bracket capture startup/stop; these are not frame-accurate media timestamps.
    public string TimingSource => "host-observed";
}
