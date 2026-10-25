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

internal sealed record TestVideoRecordingRequest(
    string Platform, string DeviceIdentifier, bool IsVirtual, string OutputPath);

internal sealed record TestVideoRecordingCompletion(bool IsComplete, string? Error = null, TimeSpan? CapturedDuration = null);

internal interface ITestVideoRecording
{
    string Source { get; }
    Task<TestVideoRecordingCompletion> StopAsync();
}

internal interface ITestVideoRecorder
{
    Task<ITestVideoRecording> StartAsync(TestVideoRecordingRequest request, CancellationToken cancellationToken);
}
