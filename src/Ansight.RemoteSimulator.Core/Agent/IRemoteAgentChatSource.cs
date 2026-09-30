using Ansight.RemoteSimulator.Core.Annotations;

namespace Ansight.RemoteSimulator.Core.Agent;

public interface IRemoteAgentChatSource
{
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
