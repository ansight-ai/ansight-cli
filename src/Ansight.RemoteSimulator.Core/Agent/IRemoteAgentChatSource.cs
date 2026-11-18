using Ansight.RemoteSimulator.Core.Annotations;

namespace Ansight.RemoteSimulator.Core.Agent;

public interface IRemoteAgentChatSource
{
    bool SupportsSubmission => true;

    bool SupportsProjects => false;

    Task<RemoteAgentProjectCatalog> ListProjectsAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(new RemoteAgentProjectCatalog(false, "Update the host to link Codex projects.", [], [], false));

    Task<RemoteAgentProjectUpdateResult> UpdateProjectAsync(
        RemoteAgentProjectUpdateRequest request,
        CancellationToken cancellationToken = default)
        => Task.FromResult(new RemoteAgentProjectUpdateResult(false, "Update the host to link Codex projects."));

    Task<RemoteAgentChatListResult> ListAgentChatsAsync(
        RemoteAgentChatListRequest request,
        CancellationToken cancellationToken = default);

    Task<RemoteAgentTaskListResult> ListAgentTasksAsync(
        RemoteAgentTaskListRequest request,
        CancellationToken cancellationToken = default);

    Task<RemoteAnnotationBatchSubmissionResult> SubmitAnnotationBatchAsync(
        RemoteAnnotationBatchSubmissionRequest request,
        CancellationToken cancellationToken = default);
}
