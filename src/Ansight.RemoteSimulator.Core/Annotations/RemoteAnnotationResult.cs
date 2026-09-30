namespace Ansight.RemoteSimulator.Core.Annotations;

public sealed record RemoteAnnotationResult(
    bool IsSuccess,
    string Message,
    string? AnnotationId = null,
    string? SessionId = null,
    string? BatchId = null,
    string? FrameId = null,
    DateTimeOffset? FrameCapturedAtUtc = null);
