using Ansight.RemoteSimulator.Core.Agent;
using Ansight.RemoteSimulator.Core.Annotations;

namespace Ansight.Host.Agents;

public interface ILocalAnnotationAgentSource : IAsyncDisposable
{
    Task<LocalAgentCatalog> CatalogAsync(string sessionId, string provider, string? projectId, CancellationToken cancellationToken);
    Task<RemoteAnnotationBatchSubmissionResult> SendAsync(string sessionId, LocalAnnotationSendRequest request, CancellationToken cancellationToken);
    Task<RemoteAnnotationBatchSubmissionResult> StatusAsync(string sessionId, string submissionId, CancellationToken cancellationToken);
}

public sealed record LocalAgentCatalog(bool IsSuccess, string Message, IReadOnlyList<RemoteAgentProject> Projects,
    IReadOnlyList<RemoteAgentChatSession> Chats, string? ProjectId = null, bool CanQueue = false, bool CanBatch = true);
public sealed record LocalAnnotationSendRequest(string SubmissionId, string AnnotationId, string Provider,
    string ProjectId, string? ChatSessionId = null, bool StartNewTask = false, bool QueueAfterCurrent = false, IReadOnlyList<string>? AnnotationIds = null);
