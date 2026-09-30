using Ansight.RemoteSimulator.Core.Agent;

namespace Ansight.RemoteSimulator.Core.Annotations;

public sealed record RemoteAnnotationBatchSubmissionResult(
    bool IsSuccess,
    string Message,
    string Status,
    string? ProviderTurnId = null,
    string? ProviderTaskId = null,
    string? ProviderTaskTitle = null,
    RemoteAgentTaskLink? TaskLink = null);
