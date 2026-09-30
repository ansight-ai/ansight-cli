namespace Ansight.RemoteSimulator.Core.Annotations;

public sealed record RemoteAnnotationBatchSubmissionRequest(
    string BatchId,
    string SessionId,
    string FrameId,
    IReadOnlyList<string> AnnotationIds,
    string Provider,
    string ChatSessionId,
    string? ChatTitle = null);
