using System.Net;
using System.Text.Json;

namespace Ansight.RemoteSimulator.Core.Recording;

public interface IRemoteRecordingReviewSource
{
    Task<RemoteRecordingEvidenceSlice?> GetEvidenceAsync(
        RemoteRecordingEvidenceQuery query,
        CancellationToken cancellationToken = default);

    Task<RemoteRecordingReviewResult> ReviewAsync(
        string operation,
        string? recordingId,
        JsonElement arguments,
        CancellationToken cancellationToken = default);
}

public sealed record RemoteRecordingEvidenceQuery(
    string RecordingId,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    DateTimeOffset CursorUtc,
    int MaximumLogCount,
    int MaximumMetricSampleCount,
    string? LogStreamId = null);

public sealed record RemoteRecordingReviewResult(HttpStatusCode StatusCode, byte[] Content);
