namespace Ansight.RemoteSimulator.Core.Annotations;

public sealed record RemoteAnnotationBatchStartResult(
    bool IsSuccess,
    string Message,
    string? BatchId = null,
    string? SessionId = null,
    string? FrameId = null,
    DateTimeOffset? FrameCapturedAtUtc = null);
