namespace Ansight.Host.Sessions;

public sealed class SessionEditingService
{
    private readonly ISessionIngestion sessionIngestion;
    private readonly ISessionEditor sessionEditor;
    private readonly WorkspaceTrendsService trendsService;

    internal SessionEditingService(
        ISessionIngestion sessionIngestion,
        ISessionEditor sessionEditor,
        WorkspaceTrendsService trendsService)
    {
        this.sessionIngestion = sessionIngestion ?? throw new ArgumentNullException(nameof(sessionIngestion));
        this.sessionEditor = sessionEditor ?? throw new ArgumentNullException(nameof(sessionEditor));
        this.trendsService = trendsService ?? throw new ArgumentNullException(nameof(trendsService));
    }

    public void AddAnalysis(string sessionId, SessionAnalysisRecord analysis)
        => sessionIngestion.AddSessionAnalysis(sessionId, analysis);

    internal void SetAppToolCatalog(string sessionId, SessionAppToolCatalogSnapshot catalog)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(catalog);
        sessionIngestion.SetSessionAppToolCatalog(sessionId.Trim(), catalog);
    }

    public OperationResult AddVisualTreeSnapshot(string sessionId, SessionVisualTreeSnapshot snapshot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(snapshot);
        return sessionIngestion.AddSessionVisualTreeSnapshot(sessionId.Trim(), snapshot);
    }

    public OperationResult AddArtifactSnapshot(
        string sessionId,
        SessionArtifactSnapshot snapshot,
        string sourceDirectoryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDirectoryPath);
        return sessionIngestion.AddSessionArtifactSnapshot(sessionId.Trim(), snapshot, sourceDirectoryPath.Trim());
    }

    public OperationResult UpdateMetadata(
        string sessionId,
        bool isPinned,
        IReadOnlyList<string>? tags,
        string? notes,
        string? name = null)
        => sessionEditor.UpdateSessionMetadata(sessionId, isPinned, tags, notes, name);

    public OperationResult UpsertAnnotation(string sessionId, SessionAnnotation annotation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(annotation);
        return sessionEditor.UpsertSessionAnnotation(sessionId.Trim(), annotation);
    }

    public OperationResult UpsertAgentTaskLink(string sessionId, SessionAgentTaskLink taskLink)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(taskLink);
        return sessionEditor.UpsertSessionAgentTaskLink(sessionId.Trim(), taskLink);
    }

    public OperationResult DeleteAnnotation(string sessionId, string annotationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(annotationId);
        return sessionEditor.DeleteSessionAnnotation(sessionId.Trim(), annotationId.Trim());
    }

    public OperationResult DeleteAnalysis(string sessionId, string analysisId)
        => sessionEditor.DeleteSessionAnalysis(sessionId, analysisId);

    public OperationResult TrimTimeline(
        string sessionId,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        SessionTimelineTrimMode mode,
        Action<SessionTimelineTrimProgress>? report = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        return sessionEditor.TrimSessionTimeline(sessionId.Trim(), startUtc, endUtc, mode, report);
    }

    public SessionNormalizationResult Normalize(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        return sessionEditor.NormalizeSession(sessionId.Trim());
    }

    public SessionOptimizationResult Optimize(string sessionId, Action<SessionOptimizationProgress>? report = null, CancellationToken cancellationToken = default)
        => sessionEditor.OptimizeSession(sessionId, report, cancellationToken);

    public SessionOptimizationResult Optimize(
        string sessionId,
        SessionOptimizationOptions options,
        Action<SessionOptimizationProgress>? report = null,
        CancellationToken cancellationToken = default)
        => sessionEditor.OptimizeSession(sessionId, options, report, cancellationToken);

    public SessionExtractionResult ExtractTimelineRange(
        string sessionId,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        string? name = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        return sessionEditor.ExtractSessionTimelineRange(sessionId.Trim(), startUtc, endUtc, name);
    }

    public SessionExtractionResult ExtractAnnotationBounds(
        string sessionId,
        string annotationId,
        string? name = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(annotationId);
        return sessionEditor.ExtractSessionAnnotationBounds(sessionId.Trim(), annotationId.Trim(), name);
    }

    public OperationResult Delete(string sessionId)
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
}
