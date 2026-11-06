using Ansight.RemoteSimulator.Core.Agent;
using Ansight.RemoteSimulator.Core.Annotations;

namespace Ansight.Cli.LocalSimulator;

internal sealed class CliRemoteAgentChatSource : IRemoteAgentChatSource
{
    private const string SubmissionUnavailable = "This Mac supports saving annotations to Ansight. Sending them to Codex or Claude is not connected in this host version.";
    private readonly RuntimeCoordinator runtime;

    public CliRemoteAgentChatSource(RuntimeCoordinator runtime)
    {
        this.runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
    }

    public bool SupportsSubmission => false;

    public Task<RemoteAgentChatListResult> ListAgentChatsAsync(
        RemoteAgentChatListRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new RemoteAgentChatListResult(false, SubmissionUnavailable, []));
    }

    public Task<RemoteAnnotationBatchSubmissionResult> SubmitAnnotationBatchAsync(
        RemoteAnnotationBatchSubmissionRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new RemoteAnnotationBatchSubmissionResult(false, SubmissionUnavailable, "unavailable"));
    }

    public Task<RemoteAgentTaskListResult> ListAgentTasksAsync(
        RemoteAgentTaskListRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var tasks = runtime.Sessions.GetSummaries()
            .Where(session => string.IsNullOrWhiteSpace(request.SessionId)
                || string.Equals(session.SessionId, request.SessionId.Trim(), StringComparison.Ordinal))
            .SelectMany(static session => session.AgentTaskLinks)
            .OrderByDescending(static link => link.UpdatedAtUtc)
            .Take(Math.Clamp(request.Limit, 1, 200))
            .Select(static link => new RemoteAgentTaskLink(
                link.LinkId, link.WorkContextId, link.SessionId, link.AnnotationBatchId,
                link.AnnotationIds, link.FrameIds, link.Source, link.Provider, link.ProviderTaskId,
                link.ProviderTurnId, link.ProviderTaskTitle, link.WorkingDirectory, link.SubmittedPrompt,
                link.Status, link.StatusMessage, link.CreatedAtUtc, link.UpdatedAtUtc))
            .ToArray();
        return Task.FromResult(new RemoteAgentTaskListResult(true, "Linked Ansight task history loaded.", tasks));
    }
}
