namespace Ansight.RemoteSimulator.Core.Recording;

public interface IRemoteRecordingSource
{
    Task<IReadOnlyList<RemoteRecordingSummary>> ListAsync(
        CancellationToken cancellationToken = default);

    Task<RemoteRecordingDetails?> GetAsync(
        string recordingId,
        CancellationToken cancellationToken = default);

    Task<RemoteRecordingFrame?> GetFrameAsync(
        string recordingId,
        string frameId,
        CancellationToken cancellationToken = default);

    Task<RemoteRecordingFrame?> GetAppIconAsync(
        string recordingId,
        CancellationToken cancellationToken = default);

    Task<RemoteRecordingEvidenceSlice?> GetEvidenceAsync(
        string recordingId,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        int maximumLogCount,
        int maximumMetricSampleCount,
        CancellationToken cancellationToken = default);
}
