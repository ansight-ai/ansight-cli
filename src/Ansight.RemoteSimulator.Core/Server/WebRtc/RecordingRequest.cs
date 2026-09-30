namespace Ansight.RemoteSimulator.Core.Server.WebRtc;

internal sealed record RecordingRequest(
    string Kind,
    string RequestId,
    string Operation,
    string? RecordingId,
    string? FrameId,
    DateTimeOffset? StartUtc,
    DateTimeOffset? EndUtc,
    int? MaximumLogCount,
    int? MaximumMetricSampleCount);
