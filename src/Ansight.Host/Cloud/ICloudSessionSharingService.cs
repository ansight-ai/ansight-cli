namespace Ansight.Host.Cloud;

public interface ICloudSessionSharingService :
    IAppOperations,
    IAccountOperations,
    IAppGraphOperations,
    ITrendsOperations,
    ITestRunOperations,
    IRunnerOperations
{
    new Task<CloudRegisteredAppQueryResult> ListRegisteredAppsAsync(
        Guid? teamId = null,
        CancellationToken cancellationToken = default)
        => Task.FromResult(CloudRegisteredAppQueryResult.Failure("Cloud app registration is not configured for this host."));

    new Task<CloudRegisteredAppOperationResult> RegisterAppAsync(
        Guid teamId,
        string appId,
        string name,
        string? platform,
        CancellationToken cancellationToken = default)
        => Task.FromResult(CloudRegisteredAppOperationResult.Failure("Cloud app registration is not configured for this host."));

    new Task<CloudRegisteredAppOperationResult> RemoveAppAsync(
        Guid teamId,
        string appId,
        CancellationToken cancellationToken = default)
        => Task.FromResult(CloudRegisteredAppOperationResult.Failure("Cloud app registration is not configured for this host."));

    new Task<CloudAccountGrantQueryResult> ListAccountGrantsAsync(
        Guid? teamId = null,
        CancellationToken cancellationToken = default)
        => Task.FromResult(CloudAccountGrantQueryResult.Failure("Cloud account grants are not configured for this host."));

    new Task<CloudAccountUsageResult> GetAccountUsageAsync(
        Guid teamId,
        CancellationToken cancellationToken = default)
        => Task.FromResult(CloudAccountUsageResult.Failure("Cloud AI usage is not configured for this host."));

    new Task<CloudAppGraphQueryResult> ListAppGraphsAsync(
        Guid teamId,
        string? query = null,
        CancellationToken cancellationToken = default)
        => Task.FromResult(CloudAppGraphQueryResult.Failure("App Graphs are not configured for this host."));

    new Task<CloudAppGraphDetailResult> GetAppGraphAsync(
        Guid appGraphId,
        bool publishedOnly = false,
        CancellationToken cancellationToken = default)
        => Task.FromResult(CloudAppGraphDetailResult.Failure("App Graphs are not configured for this host."));

    new Task<CloudAppGraphCreateResult> CreateAppGraphAsync(
        CloudAppGraphCreateRequest request,
        CancellationToken cancellationToken = default)
        => Task.FromResult(CloudAppGraphCreateResult.Failure("App Graph creation is not configured for this host."));

    new Task<CloudAppGraphObservationOperationResult> CreateAppGraphObservationAsync(
        CloudAppGraphObservationCreateRequest request,
        CancellationToken cancellationToken = default)
        => Task.FromResult(CloudAppGraphObservationOperationResult.Failure("App Graph observations are not configured for this host."));

    new Task<CloudAppGraphRunOperationResult> CreateAppGraphRunAsync(
        CloudAppGraphRunCreateRequest request,
        CancellationToken cancellationToken = default)
        => Task.FromResult(CloudAppGraphRunOperationResult.Failure("App Graph runs are not configured for this host."));

    new Task<CloudAppGraphRunOperationResult> WriteAppGraphRunStepAsync(
        CloudAppGraphRunStepWriteRequest request,
        CancellationToken cancellationToken = default)
        => Task.FromResult(CloudAppGraphRunOperationResult.Failure("App Graph runs are not configured for this host.", request.RunId));

    new Task<CloudAppGraphRunOperationResult> CompleteAppGraphRunAsync(
        Guid runId,
        string status,
        string? message,
        CancellationToken cancellationToken = default)
        => Task.FromResult(CloudAppGraphRunOperationResult.Failure("App Graph runs are not configured for this host.", runId));

    new Task<CloudTrendsUploadResult> UploadTrendsHistoryAsync(
        CloudTrendsUploadRequest request,
        CancellationToken cancellationToken = default)
        => Task.FromResult(CloudTrendsUploadResult.Failure("Cloud Trends is not configured for this host."));

    new Task<CloudTestRunUploadResult> UploadTestRunAsync(
        CloudTestRunUploadRequest request,
        CancellationToken cancellationToken = default)
        => Task.FromResult(CloudTestRunUploadResult.Failure("Cloud test tracking is not configured for this host."));

    new Task<CloudRunnerKeyQueryResult> ListRunnerKeysAsync(
        Guid teamId,
        CancellationToken cancellationToken = default)
        => Task.FromResult(CloudRunnerKeyQueryResult.Failure("Cloud runner key management is not configured for this host."));

    new Task<CloudRunnerKeyIssueResult> IssueRunnerKeyAsync(
        CloudRunnerKeyIssueRequest request,
        CancellationToken cancellationToken = default)
        => Task.FromResult(CloudRunnerKeyIssueResult.Failure("Cloud runner key management is not configured for this host."));

    new Task<CloudRunnerKeyOperationResult> RevokeRunnerKeyAsync(
        Guid keyId,
        CancellationToken cancellationToken = default)
        => Task.FromResult(CloudRunnerKeyOperationResult.Failure(keyId, "Cloud runner key management is not configured for this host."));

    Task<CloudSessionTeamQueryResult> ListTeamsAsync(
        string? query,
        CancellationToken cancellationToken = default);

    Task<CloudSessionLookupResult> FindSessionsAsync(
        CloudSessionLookupRequest request,
        CancellationToken cancellationToken = default);

    Task<CloudSessionUploadResult> ShareSessionAsync(
        CloudSessionUploadRequest request,
        CancellationToken cancellationToken = default);

    Task<CloudSessionOperationResult> NotifySessionBatchUploadedAsync(
        IReadOnlyList<Guid> sessionIds,
        CancellationToken cancellationToken = default)
        => Task.FromResult(CloudSessionOperationResult.Failure("Cloud session batch notifications are not configured for this host."));

    Task<CloudSessionOperationResult> SetSessionArchivedAsync(
        Guid sessionId,
        bool shouldArchive,
        CancellationToken cancellationToken = default)
        => Task.FromResult(CloudSessionOperationResult.Failure("Cloud session administration is not configured for this host."));

    Task<CloudSessionOperationResult> DeleteSessionAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default)
        => Task.FromResult(CloudSessionOperationResult.Failure("Cloud session administration is not configured for this host."));

    Task<CloudSessionDownloadResult> DownloadSessionAsync(
        Guid sessionId,
        string archiveFilePath,
        CancellationToken cancellationToken = default)
        => Task.FromResult(CloudSessionDownloadResult.Failure("Cloud session downloads are not configured for this host."));

    Task<CloudSessionAttachmentListResult> ListAttachmentsAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default)
        => Task.FromResult(CloudSessionAttachmentListResult.Failure("Cloud session attachments are not configured for this host."));

    Task<CloudSessionAttachmentResult> UploadAttachmentAsync(
        Guid sessionId,
        string name,
        string notes,
        string filePath,
        CancellationToken cancellationToken = default)
        => Task.FromResult(CloudSessionAttachmentResult.Failure("Cloud session attachments are not configured for this host."));

    Task<CloudSessionAttachmentResult> DownloadAttachmentAsync(
        Guid sessionId,
        Guid attachmentId,
        string filePath,
        CancellationToken cancellationToken = default)
        => Task.FromResult(CloudSessionAttachmentResult.Failure("Cloud session attachments are not configured for this host."));

    Task<CloudSessionOperationResult> DeleteAttachmentAsync(
        Guid sessionId,
        Guid attachmentId,
        CancellationToken cancellationToken = default)
        => Task.FromResult(CloudSessionOperationResult.Failure("Cloud session attachments are not configured for this host."));

    Task<CloudSessionAnalysisListResult> ListAnalysesAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default)
        => Task.FromResult(CloudSessionAnalysisListResult.Failure("Hosted session analysis is not configured for this host."));

    Task<CloudSessionAnalysisCapabilitiesResult> GetAnalysisCapabilitiesAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default)
        => Task.FromResult(CloudSessionAnalysisCapabilitiesResult.Failure("Hosted session analysis is not configured for this host."));

    Task<CloudSessionAnalysisRunResult> RunAnalysisAsync(
        CloudSessionAnalysisRunRequest request,
        CancellationToken cancellationToken = default)
        => Task.FromResult(CloudSessionAnalysisRunResult.Failure("Hosted session analysis is not configured for this host."));

    Task<CloudSessionOperationResult> SetAnalysisArchivedAsync(
        Guid runId,
        bool shouldArchive,
        CancellationToken cancellationToken = default)
        => Task.FromResult(CloudSessionOperationResult.Failure("Hosted session analysis is not configured for this host."));

    // Adapt existing providers to the focused capability interfaces.
    Task<CloudRegisteredAppQueryResult> IAppOperations.ListRegisteredAppsAsync(
        Guid? teamId,
        CancellationToken cancellationToken)
        => ListRegisteredAppsAsync(teamId, cancellationToken);

    Task<CloudRegisteredAppOperationResult> IAppOperations.RegisterAppAsync(
        Guid teamId,
        string appId,
        string name,
        string? platform,
        CancellationToken cancellationToken)
        => RegisterAppAsync(teamId, appId, name, platform, cancellationToken);

    Task<CloudRegisteredAppOperationResult> IAppOperations.RemoveAppAsync(
        Guid teamId,
        string appId,
        CancellationToken cancellationToken)
        => RemoveAppAsync(teamId, appId, cancellationToken);

    Task<CloudAccountGrantQueryResult> IAccountOperations.ListAccountGrantsAsync(
        Guid? teamId,
        CancellationToken cancellationToken)
        => ListAccountGrantsAsync(teamId, cancellationToken);

    Task<CloudAccountUsageResult> IAccountOperations.GetAccountUsageAsync(
        Guid teamId,
        CancellationToken cancellationToken)
        => GetAccountUsageAsync(teamId, cancellationToken);

    Task<CloudAppGraphQueryResult> IAppGraphOperations.ListAppGraphsAsync(
        Guid teamId,
        string? query,
        CancellationToken cancellationToken)
        => ListAppGraphsAsync(teamId, query, cancellationToken);

    Task<CloudAppGraphDetailResult> IAppGraphOperations.GetAppGraphAsync(
        Guid appGraphId,
        bool publishedOnly,
        CancellationToken cancellationToken)
        => GetAppGraphAsync(appGraphId, publishedOnly, cancellationToken);

    Task<CloudAppGraphCreateResult> IAppGraphOperations.CreateAppGraphAsync(
        CloudAppGraphCreateRequest request,
        CancellationToken cancellationToken)
        => CreateAppGraphAsync(request, cancellationToken);

    Task<CloudAppGraphObservationOperationResult> IAppGraphOperations.CreateAppGraphObservationAsync(
        CloudAppGraphObservationCreateRequest request,
        CancellationToken cancellationToken)
        => CreateAppGraphObservationAsync(request, cancellationToken);

    Task<CloudAppGraphRunOperationResult> IAppGraphOperations.CreateAppGraphRunAsync(
        CloudAppGraphRunCreateRequest request,
        CancellationToken cancellationToken)
        => CreateAppGraphRunAsync(request, cancellationToken);

    Task<CloudAppGraphRunOperationResult> IAppGraphOperations.WriteAppGraphRunStepAsync(
        CloudAppGraphRunStepWriteRequest request,
        CancellationToken cancellationToken)
        => WriteAppGraphRunStepAsync(request, cancellationToken);

    Task<CloudAppGraphRunOperationResult> IAppGraphOperations.CompleteAppGraphRunAsync(
        Guid runId,
        string status,
        string? message,
        CancellationToken cancellationToken)
        => CompleteAppGraphRunAsync(runId, status, message, cancellationToken);

    Task<CloudTrendsUploadResult> ITrendsOperations.UploadTrendsHistoryAsync(
        CloudTrendsUploadRequest request,
        CancellationToken cancellationToken)
        => UploadTrendsHistoryAsync(request, cancellationToken);

    Task<CloudTestRunUploadResult> ITestRunOperations.UploadTestRunAsync(
        CloudTestRunUploadRequest request,
        CancellationToken cancellationToken)
        => UploadTestRunAsync(request, cancellationToken);

    Task<CloudRunnerKeyQueryResult> IRunnerOperations.ListRunnerKeysAsync(
        Guid teamId,
        CancellationToken cancellationToken)
        => ListRunnerKeysAsync(teamId, cancellationToken);

    Task<CloudRunnerKeyIssueResult> IRunnerOperations.IssueRunnerKeyAsync(
        CloudRunnerKeyIssueRequest request,
        CancellationToken cancellationToken)
        => IssueRunnerKeyAsync(request, cancellationToken);

    Task<CloudRunnerKeyOperationResult> IRunnerOperations.RevokeRunnerKeyAsync(
        Guid keyId,
        CancellationToken cancellationToken)
        => RevokeRunnerKeyAsync(keyId, cancellationToken);
}
