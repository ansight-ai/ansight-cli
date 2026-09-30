namespace Ansight.RemoteSimulator.Core.Agent;

public sealed record RemoteAgentTaskListResult(
    bool IsSuccess,
    string Message,
    IReadOnlyList<RemoteAgentTaskLink> Tasks);
