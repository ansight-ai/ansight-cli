namespace Ansight.Host.Runtime.State;

using Ansight.Host;

internal interface ISessionEditor
{
    OperationResult UpdateSessionMetadata(
        string sessionId,
        bool isPinned,
        IReadOnlyList<string>? tags,
        string? notes,
        string? name = null);

    OperationResult UpsertSessionAnnotation(string sessionId, SessionAnnotation annotation);
    OperationResult UpsertSessionAgentTaskLink(string sessionId, SessionAgentTaskLink taskLink);
    OperationResult DeleteSessionAnnotation(string sessionId, string annotationId);
    OperationResult DeleteSessionAnalysis(string sessionId, string analysisId);

    OperationResult TrimSessionTimeline(
        string sessionId,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        SessionTimelineTrimMode mode,
        Action<SessionTimelineTrimProgress>? report = null);

    SessionNormalizationResult NormalizeSession(string sessionId);
    SessionNormalizationResult NormalizeSession(string sessionId, SessionOptimizationOptions options, Action<SessionOptimizationProgress>? report = null, CancellationToken cancellationToken = default);

    SessionOptimizationResult OptimizeSession(string sessionId, Action<SessionOptimizationProgress>? report = null, CancellationToken cancellationToken = default);
    SessionOptimizationResult OptimizeSession(string sessionId, SessionOptimizationOptions options, Action<SessionOptimizationProgress>? report = null, CancellationToken cancellationToken = default);

    SessionExtractionResult ExtractSessionTimelineRange(
        string sessionId,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        string? name = null);

    SessionExtractionResult ExtractSessionAnnotationBounds(
        string sessionId,
        string annotationId,
        string? name = null);

    OperationResult DeleteSession(string sessionId);
}
