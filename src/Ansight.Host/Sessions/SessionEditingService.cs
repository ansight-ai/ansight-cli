namespace Ansight.Host.Sessions;

public sealed class SessionEditingService
{
    private readonly ISessionIngestion sessionIngestion;
    private readonly SessionActivityService? activity;
    private readonly ISessionEditor sessionEditor;
    private readonly WorkspaceTrendsService trendsService;

    internal SessionEditingService(
        ISessionIngestion sessionIngestion,
        ISessionEditor sessionEditor,
        WorkspaceTrendsService trendsService, SessionActivityService? activity = null)
    {
        this.activity = activity;
        this.sessionIngestion = sessionIngestion ?? throw new ArgumentNullException(nameof(sessionIngestion));
        this.sessionEditor = sessionEditor ?? throw new ArgumentNullException(nameof(sessionEditor));
        this.trendsService = trendsService ?? throw new ArgumentNullException(nameof(trendsService));
    }

    public void AddAnalysis(string sessionId, SessionAnalysisRecord analysis)
    {
        if (activity is null) { sessionIngestion.AddSessionAnalysis(sessionId, analysis); return; }
        activity.Record("session.AddAnalysis", "session", sessionId,
            () => { sessionIngestion.AddSessionAnalysis(sessionId, analysis); return true; }, _ => "succeeded");
    }

    internal void SetUiTestRun(string sessionId, SessionUiTestRun run)
        => sessionEditor.SetSessionUiTestRun(sessionId, run);

    internal void SetAppToolCatalog(string sessionId, SessionAppToolCatalogSnapshot catalog)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(catalog);
        sessionIngestion.SetSessionAppToolCatalog(sessionId.Trim(), catalog);
    }

    private OperationResult AddVisualTreeSnapshotCore(string sessionId, SessionVisualTreeSnapshot snapshot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(snapshot);
        return sessionIngestion.AddSessionVisualTreeSnapshot(sessionId.Trim(), snapshot);
    }

    private OperationResult AddArtifactSnapshotCore(
        string sessionId,
        SessionArtifactSnapshot snapshot,
        string sourceDirectoryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDirectoryPath);
        return sessionIngestion.AddSessionArtifactSnapshot(sessionId.Trim(), snapshot, sourceDirectoryPath.Trim());
    }

    private OperationResult UpdateMetadataCore(
        string sessionId,
        bool isPinned,
        IReadOnlyList<string>? tags,
        string? notes,
        string? name = null)
        => sessionEditor.UpdateSessionMetadata(sessionId, isPinned, tags, notes, name);

    private OperationResult UpsertAnnotationCore(string sessionId, SessionAnnotation annotation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(annotation);
        return sessionEditor.UpsertSessionAnnotation(sessionId.Trim(), annotation);
    }

    private OperationResult SetAnnotationStatusCore(string sessionId, string annotationId, string? status)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(annotationId);
        if (sessionEditor is not ISessionAnnotationMutator mutator)
        {
            return OperationResult.Failure("Annotation status updates are unavailable.");
        }

        var result = mutator.MutateSessionAnnotation(sessionId.Trim(), new SessionAnnotationMutation
        {
            Kind = SessionAnnotationMutationKind.Patch,
            AnnotationId = annotationId.Trim(),
            HasStatus = true,
            Status = status
        });
        return result.IsSuccess
            ? OperationResult.Success($"Annotation '{annotationId}' status updated.")
            : OperationResult.Failure(result.Message);
    }

    private OperationResult UpsertAgentTaskLinkCore(string sessionId, SessionAgentTaskLink taskLink)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(taskLink);
        return sessionEditor.UpsertSessionAgentTaskLink(sessionId.Trim(), taskLink);
    }

    private OperationResult DeleteAnnotationCore(string sessionId, string annotationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(annotationId);
        return sessionEditor.DeleteSessionAnnotation(sessionId.Trim(), annotationId.Trim());
    }

    private OperationResult DeleteAnalysisCore(string sessionId, string analysisId)
        => sessionEditor.DeleteSessionAnalysis(sessionId, analysisId);

    private OperationResult TrimTimelineCore(
        string sessionId,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        SessionTimelineTrimMode mode,
        Action<SessionTimelineTrimProgress>? report = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        return sessionEditor.TrimSessionTimeline(sessionId.Trim(), startUtc, endUtc, mode, report);
    }

    private SessionNormalizationResult NormalizeCore(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        return sessionEditor.NormalizeSession(sessionId.Trim());
    }

    private SessionOptimizationResult OptimizeCore(string sessionId, Action<SessionOptimizationProgress>? report = null, CancellationToken cancellationToken = default)
        => sessionEditor.OptimizeSession(sessionId, report, cancellationToken);

    private SessionOptimizationResult OptimizeCore(
        string sessionId,
        SessionOptimizationOptions options,
        Action<SessionOptimizationProgress>? report = null,
        CancellationToken cancellationToken = default)
        => sessionEditor.OptimizeSession(sessionId, options, report, cancellationToken);

    private SessionExtractionResult ExtractTimelineRangeCore(
        string sessionId,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        string? name = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        return sessionEditor.ExtractSessionTimelineRange(sessionId.Trim(), startUtc, endUtc, name);
    }

    private SessionExtractionResult ExtractAnnotationBoundsCore(
        string sessionId,
        string annotationId,
        string? name = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(annotationId);
        return sessionEditor.ExtractSessionAnnotationBounds(sessionId.Trim(), annotationId.Trim(), name);
    }

    private OperationResult DeleteCore(string sessionId)
    {
        var result = sessionEditor.DeleteSession(sessionId);
        if (!result.IsSuccess)
        {
            return result;
        }

        var removedTrendsRows = trendsService.DeleteSessionHistory(sessionId);
        return OperationResult.Success(
            removedTrendsRows == 0
                ? result.Message
                : $"{result.Message} Removed {removedTrendsRows:N0} trends history row(s).");
    }

    public OperationResult AddVisualTreeSnapshot(string sessionId, SessionVisualTreeSnapshot snapshot)
        => activity is null ? AddVisualTreeSnapshotCore(sessionId, snapshot)
            : activity.Record("session.AddVisualTreeSnapshot", "session", sessionId,
                () => AddVisualTreeSnapshotCore(sessionId, snapshot), result => result.IsSuccess ? "succeeded" : "failed");

    public OperationResult AddArtifactSnapshot(
        string sessionId,
        SessionArtifactSnapshot snapshot,
        string sourceDirectoryPath)
        => activity is null ? AddArtifactSnapshotCore(sessionId, snapshot, sourceDirectoryPath)
            : activity.Record("session.AddArtifactSnapshot", "session", sessionId,
                () => AddArtifactSnapshotCore(sessionId, snapshot, sourceDirectoryPath), result => result.IsSuccess ? "succeeded" : "failed");

    public OperationResult UpdateMetadata(
        string sessionId,
        bool isPinned,
        IReadOnlyList<string>? tags,
        string? notes,
        string? name = null)
        => activity is null ? UpdateMetadataCore(sessionId, isPinned, tags, notes, name)
            : activity.Record("session.UpdateMetadata", "session", sessionId,
                () => UpdateMetadataCore(sessionId, isPinned, tags, notes, name), result => result.IsSuccess ? "succeeded" : "failed");

    public OperationResult UpsertAnnotation(string sessionId, SessionAnnotation annotation)
        => activity is null ? UpsertAnnotationCore(sessionId, annotation)
            : activity.Record("session.UpsertAnnotation", "session", sessionId,
                () => UpsertAnnotationCore(sessionId, annotation), result => result.IsSuccess ? "succeeded" : "failed");

    public OperationResult SetAnnotationStatus(string sessionId, string annotationId, string? status)
        => activity is null ? SetAnnotationStatusCore(sessionId, annotationId, status)
            : activity.Record("session.SetAnnotationStatus", "session", sessionId,
                () => SetAnnotationStatusCore(sessionId, annotationId, status), result => result.IsSuccess ? "succeeded" : "failed");

    public OperationResult UpsertAgentTaskLink(string sessionId, SessionAgentTaskLink taskLink)
        => activity is null ? UpsertAgentTaskLinkCore(sessionId, taskLink)
            : activity.Record("session.UpsertAgentTaskLink", "session", sessionId,
                () => UpsertAgentTaskLinkCore(sessionId, taskLink), result => result.IsSuccess ? "succeeded" : "failed");

    public OperationResult DeleteAnnotation(string sessionId, string annotationId)
        => activity is null ? DeleteAnnotationCore(sessionId, annotationId)
            : activity.Record("session.DeleteAnnotation", "session", sessionId,
                () => DeleteAnnotationCore(sessionId, annotationId), result => result.IsSuccess ? "succeeded" : "failed");

    public OperationResult DeleteAnalysis(string sessionId, string analysisId)
        => activity is null ? DeleteAnalysisCore(sessionId, analysisId)
            : activity.Record("session.DeleteAnalysis", "session", sessionId,
                () => DeleteAnalysisCore(sessionId, analysisId), result => result.IsSuccess ? "succeeded" : "failed");

    public OperationResult TrimTimeline(
        string sessionId,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        SessionTimelineTrimMode mode,
        Action<SessionTimelineTrimProgress>? report = null)
        => activity is null ? TrimTimelineCore(sessionId, startUtc, endUtc, mode, report)
            : activity.Record("session.TrimTimeline", "session", sessionId,
                () => TrimTimelineCore(sessionId, startUtc, endUtc, mode, report), result => result.IsSuccess ? "succeeded" : "failed");

    public SessionNormalizationResult Normalize(string sessionId)
        => activity is null ? NormalizeCore(sessionId)
            : activity.Record("session.Normalize", "session", sessionId,
                () => NormalizeCore(sessionId), result => result.IsSuccess ? "succeeded" : "failed");

    public SessionOptimizationResult Optimize(string sessionId, Action<SessionOptimizationProgress>? report = null, CancellationToken cancellationToken = default)
        => activity is null ? OptimizeCore(sessionId, report, cancellationToken)
            : activity.Record("session.Optimize", "session", sessionId,
                () => OptimizeCore(sessionId, report, cancellationToken), result => result.IsSuccess ? "succeeded" : "failed");

    public SessionOptimizationResult Optimize(
        string sessionId,
        SessionOptimizationOptions options,
        Action<SessionOptimizationProgress>? report = null,
        CancellationToken cancellationToken = default)
        => activity is null ? OptimizeCore(sessionId, options, report, cancellationToken)
            : activity.Record("session.Optimize", "session", sessionId,
                () => OptimizeCore(sessionId, options, report, cancellationToken), result => result.IsSuccess ? "succeeded" : "failed");

    public SessionExtractionResult ExtractTimelineRange(
        string sessionId,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        string? name = null)
        => activity is null ? ExtractTimelineRangeCore(sessionId, startUtc, endUtc, name)
            : activity.Record("session.ExtractTimelineRange", "session", sessionId,
                () => ExtractTimelineRangeCore(sessionId, startUtc, endUtc, name), result => result.IsSuccess ? "succeeded" : "failed");

    public SessionExtractionResult ExtractAnnotationBounds(
        string sessionId,
        string annotationId,
        string? name = null)
        => activity is null ? ExtractAnnotationBoundsCore(sessionId, annotationId, name)
            : activity.Record("session.ExtractAnnotationBounds", "session", sessionId,
                () => ExtractAnnotationBoundsCore(sessionId, annotationId, name), result => result.IsSuccess ? "succeeded" : "failed");

    public OperationResult Delete(string sessionId)
        => activity is null ? DeleteCore(sessionId)
            : activity.Record("session.Delete", "session", sessionId,
                () => DeleteCore(sessionId), result => result.IsSuccess ? "succeeded" : "failed");

}
