namespace Ansight.Host.Cloud;
public interface ISessionService
{
    bool IsConfigured { get; }

    Task<CloudSessionTeamQueryResult> ListTeamsAsync(string? query = null, CancellationToken cancellationToken = default);
    Task<CloudSessionOperationResult> NotifyBatchUploadedAsync(IReadOnlyList<Guid> sessionIds, CancellationToken cancellationToken = default);
    Task<CloudRegisteredAppQueryResult> ListRegisteredAppsAsync(Guid? teamId = null, CancellationToken cancellationToken = default);
    Task<CloudRegisteredAppOperationResult> RegisterAppAsync(Guid teamId, string appId, string name, string? platform = null, CancellationToken cancellationToken = default);
    Task<CloudRegisteredAppOperationResult> RemoveAppAsync(Guid teamId, string appId, CancellationToken cancellationToken = default);
    Task<CloudAccountGrantQueryResult> ListAccountGrantsAsync(Guid? teamId = null, CancellationToken cancellationToken = default);
    Task<CloudAccountUsageResult> GetAccountUsageAsync(Guid teamId, CancellationToken cancellationToken = default);
    Task<CloudAppGraphQueryResult> ListAppGraphsAsync(Guid teamId, string? query = null, CancellationToken cancellationToken = default);
    Task<CloudAppGraphDetailResult> GetAppGraphAsync(Guid appGraphId, bool publishedOnly = false, CancellationToken cancellationToken = default);
    Task<CloudAppGraphCreateResult> CreateAppGraphAsync(CloudAppGraphCreateRequest request, CancellationToken cancellationToken = default);
    Task<CloudAppGraphObservationOperationResult> CreateAppGraphObservationAsync(CloudAppGraphObservationCreateRequest request, CancellationToken cancellationToken = default);
    Task<CloudAppGraphRunOperationResult> CreateAppGraphRunAsync(CloudAppGraphRunCreateRequest request, CancellationToken cancellationToken = default);
    Task<CloudAppGraphRunOperationResult> WriteAppGraphRunStepAsync(CloudAppGraphRunStepWriteRequest request, CancellationToken cancellationToken = default);
    Task<CloudAppGraphRunOperationResult> CompleteAppGraphRunAsync(Guid runId, string status, string? message, CancellationToken cancellationToken = default);
    Task<CloudTrendsUploadResult> UploadTrendsHistoryAsync(CloudTrendsUploadRequest request, CancellationToken cancellationToken = default);
    Task<CloudTestRunUploadResult> UploadTestRunAsync(CloudTestRunUploadRequest request, CancellationToken cancellationToken = default);
    Task<CloudRunnerKeyQueryResult> ListRunnerKeysAsync(Guid teamId, CancellationToken cancellationToken = default);
    Task<CloudRunnerKeyIssueResult> IssueRunnerKeyAsync(CloudRunnerKeyIssueRequest request, CancellationToken cancellationToken = default);
    Task<CloudRunnerKeyOperationResult> RevokeRunnerKeyAsync(Guid keyId, CancellationToken cancellationToken = default);
    Task<CloudSessionLookupResult> ListAsync(Guid? teamId = null, string? search = null, bool includeArchived = false, int limit = 200, CancellationToken cancellationToken = default);
    Task<CloudSessionDownloadResult> DownloadAsync(Guid sessionId, string archiveFilePath, CancellationToken cancellationToken = default);
    Task<CloudSessionOperationResult> SetArchivedAsync(Guid sessionId, bool shouldArchive, CancellationToken cancellationToken = default);
    Task<CloudSessionOperationResult> DeleteAsync(Guid sessionId, CancellationToken cancellationToken = default);
    Task<CloudSessionAttachmentListResult> ListAttachmentsAsync(Guid sessionId, CancellationToken cancellationToken = default);
    Task<CloudSessionAttachmentResult> UploadAttachmentAsync(Guid sessionId, string name, string notes, string filePath, CancellationToken cancellationToken = default);
    Task<CloudSessionAttachmentResult> DownloadAttachmentAsync(Guid sessionId, Guid attachmentId, string filePath, CancellationToken cancellationToken = default);
    Task<CloudSessionOperationResult> DeleteAttachmentAsync(Guid sessionId, Guid attachmentId, CancellationToken cancellationToken = default);
    Task<CloudSessionAnalysisListResult> ListAnalysesAsync(Guid sessionId, CancellationToken cancellationToken = default);
    Task<CloudSessionAnalysisCapabilitiesResult> GetAnalysisCapabilitiesAsync(Guid sessionId, CancellationToken cancellationToken = default);
    Task<CloudSessionAnalysisRunResult> RunAnalysisAsync(CloudSessionAnalysisRunRequest request, CancellationToken cancellationToken = default);
    Task<CloudSessionOperationResult> SetAnalysisArchivedAsync(Guid runId, bool shouldArchive, CancellationToken cancellationToken = default);
    Task<SessionShareResult> ShareAsync(SessionShareRequest request, CancellationToken cancellationToken = default);
    Task<SessionShareResult> ShareAsync(SessionShareRequest request, IProgress<CloudSessionStreamUploadProgress>? progress, CancellationToken cancellationToken = default);
    Task<SessionShareResult> ShareAsync(SessionShareRequest request, IProgress<CloudSessionStreamUploadProgress>? progress, Action<SessionOptimizationProgress>? preparationProgress, CancellationToken cancellationToken = default);
    Task<SessionUrlResult> GetShareUrlAsync(string sourceSessionId, Guid? teamId = null, bool includeArchived = false, CancellationToken cancellationToken = default);
}
